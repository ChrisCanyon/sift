import { ChangeDetectionStrategy, Component, computed, inject, input, linkedSignal, signal } from '@angular/core';
import { PatternView } from '../models';
import { Hunk } from '../hunk/hunk';
import { ReviewService } from '../review.service';
import { patternReviewKeys } from '../review-keys';

@Component({
  selector: 'sift-pattern-card',
  imports: [Hunk],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './pattern-card.html',
  host: { '[class.viewed]': 'viewed()' },
  styleUrl: './pattern-card.scss',
})
export class PatternCard {
  readonly pattern = input.required<PatternView>();

  protected readonly reviewService = inject(ReviewService);
  protected readonly reviewKeys = computed(() => patternReviewKeys(this.pattern()));
  protected readonly viewed = computed(() => this.reviewKeys().every(key => this.reviewService.reviewedKeys().has(key)));
  protected readonly expanded = linkedSignal(() => !this.viewed());
  protected readonly showAllMembers = signal(false);
  protected readonly sample = computed(() => this.pattern().members[0]);
  protected readonly otherMembers = computed(() => this.pattern().members.slice(1));
  protected readonly kindLabel = computed(() => (this.pattern().kind === 'replacement' ? 'Same replacement' : 'Same lines'));

  protected toggleViewed(event: Event): void {
    event.stopPropagation();
    void this.reviewService.setReviewed(this.reviewKeys(), !this.viewed());
  }
}
