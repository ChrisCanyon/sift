import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { DiffLine, HunkView, MoveSpan, Priority, ThreadAnchor } from '../models';
import { ReviewService } from '../review.service';
import { Composer, ComposerSubmission } from '../thread/composer';
import { Thread } from '../thread/thread';
import { highlightFragment, languageFor } from './syntax';

interface Segment {
  text: string;
  changed: boolean;
  html?: string | null;
}

interface RenderedLine {
  line: DiffLine;
  segments: Segment[];
  move?: MoveSpan;
  startsMove?: boolean;
}

const maximumHighlightedLineLength = 2000;
const contextStep = 20;

/** Splits paired removed/added lines into unchanged prefix, changed middle and unchanged suffix. */
function highlightChanges(lines: DiffLine[]): RenderedLine[] {
  const rendered: RenderedLine[] = lines.map(line => ({ line, segments: [{ text: line.text, changed: false }] }));
  let index = 0;
  while (index < lines.length) {
    if (lines[index].kind !== 'removed') {
      index++;
      continue;
    }
    const removedStart = index;
    while (index < lines.length && lines[index].kind === 'removed') index++;
    const addedStart = index;
    while (index < lines.length && lines[index].kind === 'added') index++;
    const pairCount = Math.min(addedStart - removedStart, index - addedStart);
    for (let pair = 0; pair < pairCount; pair++) {
      const removed = rendered[removedStart + pair];
      const added = rendered[addedStart + pair];
      [removed.segments, added.segments] = splitPair(removed.line.text, added.line.text);
    }
  }
  return rendered;
}

function splitPair(oldText: string, newText: string): [Segment[], Segment[]] {
  if (oldText.length > maximumHighlightedLineLength || newText.length > maximumHighlightedLineLength) {
    return [[{ text: oldText, changed: false }], [{ text: newText, changed: false }]];
  }
  let prefix = 0;
  while (prefix < oldText.length && prefix < newText.length && oldText[prefix] === newText[prefix]) prefix++;
  let suffix = 0;
  while (
    suffix < oldText.length - prefix &&
    suffix < newText.length - prefix &&
    oldText[oldText.length - 1 - suffix] === newText[newText.length - 1 - suffix]
  ) suffix++;
  const toSegments = (text: string): Segment[] =>
    [
      { text: text.slice(0, prefix), changed: false },
      { text: text.slice(prefix, text.length - suffix), changed: true },
      { text: text.slice(text.length - suffix), changed: false },
    ].filter(segment => segment.text.length > 0);
  return [toSegments(oldText), toSegments(newText)];
}

@Component({
  selector: 'sift-hunk',
  imports: [Composer, Thread],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './hunk.html',
  styleUrl: './hunk.scss',
})
export class Hunk {
  readonly hunk = input.required<HunkView>();
  readonly filePriority = input.required<Priority>();
  readonly filePath = input.required<string>();
  readonly patternId = input<string>();

  protected readonly reviewService = inject(ReviewService);
  protected readonly composerLine = signal<DiffLine | null | undefined>(undefined);

  protected readonly header = computed(() => {
    const hunk = this.hunk();
    return `@@ -${hunk.oldStart},${hunk.oldLineCount} +${hunk.newStart},${hunk.newLineCount} @@ ${hunk.sectionHeader}`;
  });
  protected readonly renderedLines = computed(() => {
    const language = languageFor(this.filePath());
    const rendered = highlightChanges(this.hunk().lines);
    for (const move of this.hunk().moves ?? []) {
      for (let index = move.startIndex; index < move.startIndex + move.lineCount; index++) {
        rendered[index].move = move;
        // Moved text is unchanged, so intra-line change marks would be misleading.
        rendered[index].segments = [{ text: rendered[index].line.text, changed: false }];
      }
      rendered[move.startIndex].startsMove = true;
    }
    for (const line of rendered) {
      for (const segment of line.segments) {
        segment.html = highlightFragment(segment.text, language);
      }
    }
    return rendered;
  });
  protected readonly showsOwnPriority = computed(() => this.hunk().priority !== this.filePriority());
  protected readonly threads = computed(() => this.reviewService.threadsByHunkId().get(this.hunk().id) ?? []);
  protected readonly aiNotes = computed(() => (this.reviewService.review()?.threads ?? []).filter(note => note.hunkId === this.hunk().id));
  protected readonly threadLineKeys = computed(() =>
    new Set(this.threads().map(thread => `${thread.anchor.side}:${thread.anchor.line}`)));

  protected readonly contextAbove = signal<RenderedLine[]>([]);
  protected readonly contextBelow = signal<RenderedLine[]>([]);
  protected readonly reachedEndOfFile = signal(false);
  protected readonly expanding = signal(false);

  // Deleted files only exist on the old side.
  private readonly contextSide = computed<'new' | 'old'>(() => (this.hunk().newStart === 0 ? 'old' : 'new'));
  private readonly firstShownLine = computed(() => {
    const hunk = this.hunk();
    return (this.contextSide() === 'new' ? hunk.newStart : hunk.oldStart) - this.contextAbove().length;
  });
  protected readonly canExpandAbove = computed(() => this.firstShownLine() > 1);

  protected async expandAbove(): Promise<void> {
    const end = this.firstShownLine() - 1;
    const start = Math.max(1, end - contextStep + 1);
    const lines = await this.fetchContext(start, end);
    this.contextAbove.update(existing => [...lines, ...existing]);
  }

  protected async expandBelow(): Promise<void> {
    const hunk = this.hunk();
    const lastHunkLine = this.contextSide() === 'new' ? hunk.newStart + hunk.newLineCount - 1 : hunk.oldStart + hunk.oldLineCount - 1;
    const start = lastHunkLine + this.contextBelow().length + 1;
    const lines = await this.fetchContext(start, start + contextStep - 1);
    if (lines.length < contextStep) {
      this.reachedEndOfFile.set(true);
    }
    this.contextBelow.update(existing => [...existing, ...lines]);
  }

  // Lines outside a hunk are unchanged, so old and new numbers differ by the hunk's fixed offset on that side.
  private async fetchContext(start: number, end: number): Promise<RenderedLine[]> {
    if (end < start) {
      return [];
    }
    this.expanding.set(true);
    try {
      const hunk = this.hunk();
      const side = this.contextSide();
      const result = await this.reviewService.getFileLines(this.filePath(), side, start, end);
      const isAbove = end < (side === 'new' ? hunk.newStart : hunk.oldStart);
      const newMinusOld = isAbove
        ? hunk.newStart - hunk.oldStart
        : (hunk.newStart + hunk.newLineCount) - (hunk.oldStart + hunk.oldLineCount);
      const language = languageFor(this.filePath());
      return result.lines.map(({ number, text }) => {
        const line: DiffLine = {
          kind: 'context',
          text,
          newLineNumber: side === 'new' ? number : number + newMinusOld,
          oldLineNumber: side === 'old' ? number : number - newMinusOld,
          noNewlineAtEnd: false,
        };
        return { line, segments: [{ text, changed: false, html: highlightFragment(text, language) }] };
      });
    } finally {
      this.expanding.set(false);
    }
  }

  protected moveLabel(move: MoveSpan): string {
    const location = move.otherPath === this.filePath() ? `line ${move.otherLine}` : `${move.otherPath}:${move.otherLine}`;
    const lines = `${move.lineCount} line${move.lineCount === 1 ? '' : 's'}`;
    return move.direction === 'from' ? `↳ ${lines} moved from ${location}, unchanged` : `↳ ${lines} moved to ${location}, unchanged`;
  }

  protected openComposer(line: DiffLine | null): void {
    this.composerLine.set(line);
  }

  protected hasThreadOn(line: DiffLine): boolean {
    const side = line.newLineNumber !== undefined ? 'new' : 'old';
    return this.threadLineKeys().has(`${side}:${line.newLineNumber ?? line.oldLineNumber}`);
  }

  protected async submit(submission: ComposerSubmission): Promise<void> {
    const line = this.composerLine() ?? null;
    const anchor: ThreadAnchor = {
      filePath: this.filePath(),
      hunkId: this.hunk().id,
      patternId: this.patternId(),
      line: line ? (line.newLineNumber ?? line.oldLineNumber) : undefined,
      side: line ? (line.newLineNumber !== undefined ? 'new' : 'old') : undefined,
      lineText: line?.text,
    };
    await this.reviewService.createThread(submission.kind, anchor, submission.body);
    this.composerLine.set(undefined);
  }
}
