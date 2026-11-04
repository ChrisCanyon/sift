import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';
import { ThreadKind } from '../models';

export interface ComposerSubmission {
  kind: ThreadKind;
  body: string;
}

const kindLabels: { kind: ThreadKind; label: string; hint: string }[] = [
  { kind: 'ask', label: 'Ask Claude', hint: 'Claude answers here' },
  { kind: 'change', label: 'Request change', hint: 'Claude edits the code' },
  { kind: 'pr', label: 'PR comment', hint: 'Kept local until you push it' },
];

@Component({
  selector: 'sift-composer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (showKinds()) {
      <div class="kinds">
        @for (option of kindOptions; track option.kind) {
          <button type="button" [class.selected]="kind() === option.kind" [title]="option.hint" (click)="kind.set(option.kind)">
            {{ option.label }}
          </button>
        }
      </div>
    }
    <textarea
      #textarea
      [placeholder]="placeholder()"
      [value]="body()"
      (input)="body.set(textarea.value)"
      (keydown.control.enter)="submit()"
      (keydown.escape)="cancelled.emit()"
      rows="3"
      autofocus
    ></textarea>
    <div class="actions">
      <span class="hint">Ctrl+Enter to send · Esc to cancel</span>
      <button type="button" class="secondary" (click)="cancelled.emit()">Cancel</button>
      <button type="button" class="primary" [disabled]="!body().trim()" (click)="submit()">Send</button>
    </div>
  `,
  styleUrl: './composer.scss',
})
export class Composer {
  readonly showKinds = input(true);
  readonly initialKind = input<ThreadKind>('ask');
  readonly placeholder = input('Ask Claude about this change, request an edit, or draft a PR comment…');
  readonly submitted = output<ComposerSubmission>();
  readonly cancelled = output<void>();

  protected readonly kindOptions = kindLabels;
  protected readonly kind = signal<ThreadKind>('ask');
  protected readonly body = signal('');

  ngOnInit(): void {
    this.kind.set(this.initialKind());
  }

  protected submit(): void {
    const body = this.body().trim();
    if (body) {
      this.submitted.emit({ kind: this.kind(), body });
      this.body.set('');
    }
  }
}
