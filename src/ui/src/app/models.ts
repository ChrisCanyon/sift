export type Priority = 'critical' | 'high' | 'normal' | 'low';
export type FileChangeKind = 'modified' | 'added' | 'deleted' | 'renamed' | 'copied';
export type DiffLineKind = 'context' | 'added' | 'removed';
export type Classifier = 'ai' | 'rule' | 'none';
export type PatternKind = 'replacement' | 'repeatedLines';

export interface MoveSpan {
  blockId: string;
  startIndex: number;
  lineCount: number;
  direction: 'from' | 'to';
  otherPath: string;
  otherLine: number;
}

export interface DiffLine {
  kind: DiffLineKind;
  text: string;
  oldLineNumber?: number;
  newLineNumber?: number;
  noNewlineAtEnd: boolean;
}

export interface HunkView {
  id: string;
  oldStart: number;
  oldLineCount: number;
  newStart: number;
  newLineCount: number;
  sectionHeader: string;
  priority: Priority;
  classifiedBy: Classifier;
  reason?: string;
  risk?: string;
  lines: DiffLine[];
  moves: MoveSpan[];
}

export interface FileView {
  path: string;
  oldPath?: string;
  change: FileChangeKind;
  binary: boolean;
  added: number;
  removed: number;
  priority: Priority;
  classifiedBy: Classifier;
  reason?: string;
  risk?: string;
  totalHunkCount: number;
  lineEndingChangedLines: number;
  hunks: HunkView[];
}

export interface PatternMemberView {
  path: string;
  hunk: HunkView;
}

export interface PatternView {
  id: string;
  kind: PatternKind;
  description: string;
  priority: Priority;
  classifiedBy: Classifier;
  reason?: string;
  risk?: string;
  fileCount: number;
  members: PatternMemberView[];
}

export interface BucketView {
  id: string;
  title: string;
  description?: string;
  collapsed: boolean;
  hunkCount: number;
  patterns: PatternView[];
  files: FileView[];
}

export interface ThreadMessage {
  author: string;
  body: string;
  at?: string;
}

export interface ReviewThread {
  hunkId: string;
  line?: number;
  messages: ThreadMessage[];
}

export interface ReviewSource {
  repoPath: string;
  baseSha: string;
  headSha: string;
  baseRef?: string;
  headRef?: string;
  prUrl?: string;
  prNumber?: number;
  compareUrl?: string;
  diffBase?: 'mergeBase' | 'direct';
}

export interface ReviewView {
  title?: string;
  summary?: string;
  source: ReviewSource;
  fromSha: string;
  buckets: BucketView[];
  threads: ReviewThread[];
  unmatchedHunkIds: string[];
  unmatchedFilePaths: string[];
  unmatchedPatternIds: string[];
  reconciliation: Reconciliation;
}

export interface ReviewListing {
  path: string;
  title?: string;
  repoName: string;
  prNumber?: number;
  prUrl?: string;
  headSha: string;
  modified: string;
  error?: string;
  baseRef?: string;
  headRef?: string;
}

export interface ReviewList {
  current?: string;
  reviews: ReviewListing[];
}

export type ThreadKind = 'ask' | 'change' | 'pr';
export type ThreadStatus = 'open' | 'answered' | 'done' | 'pushed';

export interface ThreadAnchor {
  filePath: string;
  hunkId?: string;
  patternId?: string;
  line?: number;
  side?: 'new' | 'old';
  lineText?: string;
}

export interface UserThread {
  id: string;
  kind: ThreadKind;
  status: ThreadStatus;
  anchor: ThreadAnchor;
  createdAt: string;
  messages: ThreadMessage[];
}

export interface ReviewState {
  reviewedKeys: string[];
  threads: UserThread[];
}

export interface FileLines {
  totalLines: number;
  lines: { number: number; text: string }[];
}

export interface ReconciliationProblem {
  kind: 'missingFile' | 'unexpectedFile' | 'lineCountMismatch' | 'notPlaced';
  path: string;
  detail: string;
}

export interface Reconciliation {
  fileCount: number;
  addedLines: number;
  removedLines: number;
  problems: ReconciliationProblem[];
}
