import { ChangeDetectionStrategy, Component, computed, inject, linkedSignal, OnInit } from '@angular/core';
import { BucketView, FileView, ReviewListing, ReviewView, UserThread } from './models';
import { bucketReviewKeys, fileReviewKeys } from './review-keys';
import { ReviewService } from './review.service';
import { FileCard } from './file-card/file-card';
import { PatternCard } from './pattern-card/pattern-card';

@Component({
  selector: 'app-root',
  imports: [FileCard, PatternCard],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(document:keydown)': 'handleShortcut($event)' },
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App implements OnInit {
  protected readonly reviewService = inject(ReviewService);
  protected readonly review = this.reviewService.review;

  // Claude may rewrite the open review; keep the reviewer's expand/collapse choices when it does.
  protected readonly expandedBucketIds = linkedSignal<{ path: string | null; review: ReviewView | null }, Set<string>>({
    source: () => ({ path: this.reviewService.selectedPath(), review: this.review() }),
    computation: (source, previous) =>
      previous && previous.source.path === source.path && previous.source.review !== null
        ? previous.value
        : new Set((source.review?.buckets ?? []).filter(bucket => !bucket.collapsed).map(bucket => bucket.id)),
  });

  protected readonly totals = computed(() => {
    const buckets = this.review()?.buckets ?? [];
    return {
      hunks: buckets.reduce((count, bucket) => count + bucket.hunkCount, 0),
      hiddenHunks: buckets.filter(bucket => !this.expandedBucketIds().has(bucket.id)).reduce((count, bucket) => count + bucket.hunkCount, 0),
    };
  });

  protected readonly progress = computed(() => {
    const reviewedKeys = this.reviewService.reviewedKeys();
    const allKeys = (this.review()?.buckets ?? []).flatMap(bucketReviewKeys);
    return { reviewed: allKeys.filter(key => reviewedKeys.has(key)).length, total: allKeys.length };
  });

  protected readonly rangeLabel = computed(() => {
    const source = this.review()?.source;
    if (!source || source.prUrl || !source.baseRef || !source.headRef) {
      return null;
    }
    return source.diffBase === 'direct' ? `${source.baseRef}..${source.headRef} (direct)` : `${source.baseRef}...${source.headRef}`;
  });

  protected readonly unsortedHunkCount = computed(() =>
    this.review()?.buckets.find(bucket => bucket.id === 'unsorted')?.hunkCount ?? 0);

  protected jumpToBucket(bucketId: string): void {
    if (!this.expandedBucketIds().has(bucketId)) {
      this.toggleBucket(bucketId);
    }
    requestAnimationFrame(() => document.getElementById(`bucket-${bucketId}`)?.scrollIntoView({ block: 'start' }));
  }

  protected readonly activeThreads = computed(() =>
    this.reviewService.state().threads.filter(thread => thread.status === 'open' || thread.status === 'answered'));

  protected readonly hasUnmatched = computed(() => {
    const review = this.review();
    return !!review && (review.unmatchedHunkIds.length > 0 || review.unmatchedFilePaths.length > 0 || review.unmatchedPatternIds.length > 0);
  });

  ngOnInit(): void {
    void this.reviewService.loadReviewList().catch(error => this.reviewService.error.set(String(error)));
  }

  protected toggleBucket(bucketId: string): void {
    this.expandedBucketIds.update(expanded => {
      const next = new Set(expanded);
      if (!next.delete(bucketId)) {
        next.add(bucketId);
      }
      return next;
    });
  }

  protected jumpToElement(bucket: BucketView, elementId: string): void {
    if (!this.expandedBucketIds().has(bucket.id)) {
      this.toggleBucket(bucket.id);
    }
    // Wait a frame so a just-expanded bucket has rendered.
    requestAnimationFrame(() => document.getElementById(elementId)?.scrollIntoView({ block: 'start' }));
  }

  protected patternElementId(bucket: BucketView, patternId: string): string {
    return `pattern-${bucket.id}-${patternId}`;
  }

  protected fileElementId(bucket: BucketView, file: FileView): string {
    return `file-${bucket.id}-${file.path}`;
  }

  protected isFileViewed(file: FileView): boolean {
    const reviewedKeys = this.reviewService.reviewedKeys();
    return fileReviewKeys(file).every(key => reviewedKeys.has(key));
  }

  protected jumpToThread(thread: UserThread): void {
    const review = this.review();
    const bucket = review?.buckets.find(candidate =>
      candidate.files.some(file => file.hunks.some(hunk => hunk.id === thread.anchor.hunkId)) ||
      candidate.patterns.some(pattern => pattern.id === thread.anchor.patternId));
    if (!bucket) {
      return;
    }
    const file = bucket.files.find(candidate => candidate.hunks.some(hunk => hunk.id === thread.anchor.hunkId));
    this.jumpToElement(bucket, file ? this.fileElementId(bucket, file) : this.patternElementId(bucket, thread.anchor.patternId!));
  }

  protected handleShortcut(event: KeyboardEvent): void {
    const target = event.target as HTMLElement | null;
    if (event.ctrlKey || event.metaKey || event.altKey || target?.closest('input, textarea, select')) {
      return;
    }

    const cards = Array.from(document.querySelectorAll<HTMLElement>('sift-file-card, sift-pattern-card'));
    if (cards.length === 0) {
      return;
    }

    const currentIndex = this.resolveCurrentCardIndex(cards);

    switch (event.key) {
      case 'j':
        this.focusCard(cards[Math.min(currentIndex + 1, cards.length - 1)]);
        break;
      case 'k':
        this.focusCard(cards[Math.max(currentIndex - 1, 0)]);
        break;
      case 'v':
        cards[currentIndex].querySelector<HTMLElement>('label.viewed')?.click();
        break;
      case 'o':
        cards[currentIndex].querySelector<HTMLElement>('header')?.click();
        break;
      default:
        return;
    }
    event.preventDefault();
  }

  private currentCard: HTMLElement | null = null;

  // Keyboard focus wins; otherwise the card at the top of the content pane is current.
  private resolveCurrentCardIndex(cards: HTMLElement[]): number {
    const focusedIndex = this.currentCard ? cards.indexOf(this.currentCard) : -1;
    if (focusedIndex >= 0) {
      return focusedIndex;
    }
    const containerTop = document.querySelector('.content')?.getBoundingClientRect().top ?? 0;
    return Math.max(0, cards.filter(card => card.getBoundingClientRect().top <= containerTop + 8).length - 1);
  }

  private focusCard(card: HTMLElement): void {
    this.currentCard?.classList.remove('keyboard-current');
    this.currentCard = card;
    card.classList.add('keyboard-current');
    card.scrollIntoView({ block: 'nearest' });
  }

  protected bucketSummary(bucket: BucketView): string {
    const parts = [
      countLabel(bucket.patterns.length, 'pattern'),
      countLabel(bucket.files.length, 'file'),
      countLabel(bucket.hunkCount, 'hunk'),
    ];
    return parts.filter(part => part !== null).join(' · ');
  }

  protected listingLabel(listing: ReviewListing): string {
    const subject = listing.prNumber
      ? `#${listing.prNumber}`
      : listing.baseRef && listing.headRef ? `${listing.baseRef}...${listing.headRef}` : listing.headSha.slice(0, 8);
    const title = listing.title ? ` · ${listing.title}` : '';
    return `${listing.repoName} ${subject}${title}${listing.error ? ' (unreadable)' : ''}`;
  }

  protected fileName(path: string): string {
    return path.slice(path.lastIndexOf('/') + 1);
  }

  protected selectReview(path: string): void {
    void this.reviewService.openReview(path);
  }
}

function countLabel(count: number, noun: string): string | null {
  return count === 0 ? null : `${count} ${noun}${count === 1 ? '' : 's'}`;
}
