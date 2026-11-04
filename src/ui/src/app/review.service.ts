import { computed, Injectable, signal } from '@angular/core';
import { FileLines, ReviewList, ReviewState, ReviewView, ThreadAnchor, ThreadKind, ThreadStatus, UserThread } from './models';

const emptyState: ReviewState = { reviewedKeys: [], threads: [] };

function samePath(first: string | null | undefined, second: string | null | undefined): boolean {
  return !!first && !!second && first.replaceAll('/', '\\').toLowerCase() === second.replaceAll('/', '\\').toLowerCase();
}

@Injectable({ providedIn: 'root' })
export class ReviewService {
  readonly reviews = signal<ReviewList>({ reviews: [] });
  readonly review = signal<ReviewView | null>(null);
  readonly state = signal<ReviewState>(emptyState);
  readonly selectedPath = signal<string | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);
  readonly listeningReviewPaths = signal<string[]>([]);

  readonly isClaudeListening = computed(() => this.isListening(this.selectedPath()));
  readonly listenCommand = computed(() => `/sift listen "${this.selectedPath() ?? ''}"`);

  readonly reviewedKeys = computed(() => new Set(this.state().reviewedKeys));
  readonly threadsByHunkId = computed(() => {
    const threadsByHunkId = new Map<string, UserThread[]>();
    for (const thread of this.state().threads) {
      const key = thread.anchor.hunkId ?? `file:${thread.anchor.filePath}`;
      threadsByHunkId.set(key, [...(threadsByHunkId.get(key) ?? []), thread]);
    }
    return threadsByHunkId;
  });
  readonly unpushedPrCommentCount = computed(
    () => this.state().threads.filter(thread => thread.kind === 'pr' && thread.status !== 'pushed').length);

  private events?: EventSource;

  async loadReviewList(): Promise<void> {
    const list = await this.getJson<ReviewList>('/api/reviews');
    this.reviews.set(list);
    if (this.selectedPath() === null) {
      const initialPath = new URLSearchParams(location.search).get('review') ?? list.current ?? list.reviews[0]?.path ?? null;
      if (initialPath) {
        await this.openReview(initialPath);
      }
    }
    this.listenForChanges();
    await this.loadListeners();
  }

  private async loadListeners(): Promise<void> {
    this.listeningReviewPaths.set((await this.getJson<{ reviews: string[] }>('/api/listeners')).reviews);
  }

  isListening(path: string | null | undefined): boolean {
    return this.listeningReviewPaths().some(listeningPath => samePath(listeningPath, path));
  }

  async copyListenCommand(): Promise<void> {
    await navigator.clipboard.writeText(this.listenCommand());
    this.notice.set('Copied. Paste it into any Claude Code session to start listening on this review.');
  }

  async openReview(path: string): Promise<void> {
    this.selectedPath.set(path);
    this.loading.set(true);
    this.error.set(null);
    history.replaceState(null, '', `?review=${encodeURIComponent(path)}`);
    try {
      const [review, state] = await Promise.all([
        this.getJson<ReviewView>(`/api/review?path=${encodeURIComponent(path)}`),
        this.getJson<ReviewState>(`/api/state?path=${encodeURIComponent(path)}`),
      ]);
      this.review.set(review);
      this.state.set(state);
    } catch (error) {
      this.review.set(null);
      this.error.set(error instanceof Error ? error.message : String(error));
    } finally {
      this.loading.set(false);
    }
  }

  async setReviewed(keys: string[], reviewed: boolean): Promise<void> {
    this.state.set(await this.post<ReviewState>('/api/state/reviewed', { keys, reviewed }));
  }

  async createThread(kind: ThreadKind, anchor: ThreadAnchor, body: string): Promise<void> {
    await this.post('/api/threads', { kind, anchor, body });
    await this.reloadState();
  }

  async reply(threadId: string, body: string): Promise<void> {
    await this.post(`/api/threads/${threadId}/messages`, { author: 'you', body });
    await this.reloadState();
  }

  async setThreadStatus(threadId: string, status: ThreadStatus): Promise<void> {
    await this.post(`/api/threads/${threadId}/status`, { status });
    await this.reloadState();
  }

  async pushPrComments(threadIds?: string[]): Promise<void> {
    const result = await this.post<{ queued: number }>('/api/push', { threadIds });
    const comments = `${result.queued} comment${result.queued === 1 ? '' : 's'}`;
    this.notice.set(this.isClaudeListening()
      ? `Sent ${comments} to Claude to post on the PR.`
      : `Queued ${comments}. No Claude is listening on this review yet; they post once one does.`);
  }

  async finishReview(): Promise<void> {
    await this.post('/api/done', {});
    this.notice.set(this.isClaudeListening() ? 'Told Claude the review is done.' : 'Marked done. No Claude is listening on this review.');
  }

  async getFileLines(path: string, side: 'new' | 'old', start: number, end: number): Promise<FileLines> {
    const query = new URLSearchParams({ review: this.selectedPath() ?? '', path, side, start: String(start), end: String(end) });
    return this.getJson<FileLines>(`/api/file-lines?${query}`);
  }

  private async reloadState(): Promise<void> {
    const path = this.selectedPath();
    if (path) {
      this.state.set(await this.getJson<ReviewState>(`/api/state?path=${encodeURIComponent(path)}`));
    }
  }

  private listenForChanges(): void {
    if (this.events) {
      return;
    }
    this.events = new EventSource('/api/events');
    this.events.addEventListener('state-changed', event => {
      if (samePath(JSON.parse((event as MessageEvent).data).review, this.selectedPath())) {
        void this.reloadState();
      }
    });
    this.events.addEventListener('listeners-changed', event => {
      this.listeningReviewPaths.set(JSON.parse((event as MessageEvent).data).reviews);
    });
    this.events.addEventListener('review-changed', event => {
      const changedPath = JSON.parse((event as MessageEvent).data).review;
      if (samePath(changedPath, this.selectedPath())) {
        void this.refreshReview();
      }
      void this.getJson<ReviewList>('/api/reviews').then(list => this.reviews.set(list));
    });
  }

  private async refreshReview(): Promise<void> {
    const path = this.selectedPath();
    if (!path) {
      return;
    }
    try {
      this.review.set(await this.getJson<ReviewView>(`/api/review?path=${encodeURIComponent(path)}`));
      this.notice.set('Review updated by Claude.');
    } catch (error) {
      this.error.set(error instanceof Error ? error.message : String(error));
    }
  }

  private async post<T>(url: string, body: object): Promise<T> {
    const response = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Sift': '1' },
      body: JSON.stringify({ review: this.selectedPath(), ...body }),
    });
    return this.readResponse<T>(response);
  }

  private async getJson<T>(url: string): Promise<T> {
    return this.readResponse<T>(await fetch(url));
  }

  private async readResponse<T>(response: Response): Promise<T> {
    const text = await response.text();
    const body = text ? JSON.parse(text) : undefined;
    if (!response.ok) {
      throw new Error(body?.error ?? `${response.status} ${response.statusText}`);
    }
    return body as T;
  }
}
