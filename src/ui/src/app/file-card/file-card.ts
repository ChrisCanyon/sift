import { ChangeDetectionStrategy, Component, computed, inject, input, linkedSignal } from '@angular/core';
import { FileView } from '../models';
import { Hunk } from '../hunk/hunk';
import { ReviewService } from '../review.service';
import { fileReviewKeys } from '../review-keys';

const changeLabels: Record<FileView['change'], string> = {
  modified: 'M',
  added: 'A',
  deleted: 'D',
  renamed: 'R',
  copied: 'C',
};

@Component({
  selector: 'sift-file-card',
  imports: [Hunk],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './file-card.html',
  host: { '[class.viewed]': 'viewed()' },
  styleUrl: './file-card.scss',
})
export class FileCard {
  readonly file = input.required<FileView>();
  readonly startCollapsed = input(false);

  protected readonly reviewService = inject(ReviewService);
  protected readonly reviewKeys = computed(() => fileReviewKeys(this.file()));
  protected readonly viewed = computed(() => this.reviewKeys().every(key => this.reviewService.reviewedKeys().has(key)));
  protected readonly expanded = linkedSignal(() => !this.startCollapsed() && !this.viewed());
  protected readonly threadCount = computed(() =>
    this.file().hunks.reduce((count, hunk) => count + (this.reviewService.threadsByHunkId().get(hunk.id)?.length ?? 0), 0));
  protected readonly changeLabel = computed(() => changeLabels[this.file().change]);
  protected readonly isPartial = computed(() => this.file().hunks.length > 0 && this.file().hunks.length < this.file().totalHunkCount);
  protected readonly estimatedHeight = computed(() =>
    this.file().hunks.reduce((height, hunk) => height + 28 + hunk.lines.length * 20, 0));

  protected toggleViewed(event: Event): void {
    event.stopPropagation();
    void this.reviewService.setReviewed(this.reviewKeys(), !this.viewed());
  }
}
