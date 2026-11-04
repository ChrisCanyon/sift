import { BucketView, FileView, PatternView } from './models';

export function fileReviewKeys(file: FileView): string[] {
  return file.hunks.length > 0 ? file.hunks.map(hunk => hunk.id) : [`file:${file.path}`];
}

export function patternReviewKeys(pattern: PatternView): string[] {
  return pattern.members.map(member => member.hunk.id);
}

export function bucketReviewKeys(bucket: BucketView): string[] {
  return [...bucket.patterns.flatMap(patternReviewKeys), ...bucket.files.flatMap(fileReviewKeys)];
}
