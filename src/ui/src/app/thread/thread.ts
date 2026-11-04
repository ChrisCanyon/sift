import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { UserThread } from '../models';
import { ReviewService } from '../review.service';
import { Composer, ComposerSubmission } from './composer';

const kindLabels = { ask: 'Question', change: 'Change request', pr: 'PR comment' } as const;

@Component({
  selector: 'sift-thread',
  imports: [Composer, DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './thread.html',
  styleUrl: './thread.scss',
})
export class Thread {
  readonly thread = input.required<UserThread>();

  protected readonly reviewService = inject(ReviewService);
  protected readonly replying = signal(false);
  protected readonly kindLabel = computed(() => kindLabels[this.thread().kind]);
  protected readonly isWaitingForClaude = computed(() => {
    const thread = this.thread();
    return thread.kind !== 'pr' && thread.status === 'open';
  });
  protected readonly isResolved = computed(() => this.thread().status === 'done' || this.thread().status === 'pushed');
  protected readonly collapsed = signal(false);

  ngOnInit(): void {
    this.collapsed.set(this.isResolved());
  }

  protected async sendReply(submission: ComposerSubmission): Promise<void> {
    await this.reviewService.reply(this.thread().id, submission.body);
    this.replying.set(false);
  }
}
