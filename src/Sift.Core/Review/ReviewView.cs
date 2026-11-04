using Sift.Core.Analysis;
using Sift.Core.Diff;

namespace Sift.Core.Review;

public enum Classifier
{
    Ai,
    Rule,
    None,
}

public sealed record HunkView(
    string Id,
    int OldStart,
    int OldLineCount,
    int NewStart,
    int NewLineCount,
    string SectionHeader,
    Priority Priority,
    Classifier ClassifiedBy,
    string? Reason,
    string? Risk,
    IReadOnlyList<DiffLine> Lines,
    IReadOnlyList<MoveSpan> Moves);

public sealed record FileView(
    string Path,
    string? OldPath,
    FileChangeKind Change,
    bool Binary,
    int Added,
    int Removed,
    Priority Priority,
    Classifier ClassifiedBy,
    string? Reason,
    string? Risk,
    int TotalHunkCount,
    int LineEndingChangedLines,
    IReadOnlyList<HunkView> Hunks);

public sealed record PatternMemberView(string Path, HunkView Hunk);

public sealed record PatternView(
    string Id,
    PatternKind Kind,
    string Description,
    Priority Priority,
    Classifier ClassifiedBy,
    string? Reason,
    string? Risk,
    int FileCount,
    IReadOnlyList<PatternMemberView> Members);

public sealed record BucketView(
    string Id,
    string Title,
    string? Description,
    bool Collapsed,
    int HunkCount,
    IReadOnlyList<PatternView> Patterns,
    IReadOnlyList<FileView> Files);

public sealed record ReviewView(
    string? Title,
    string? Summary,
    ReviewSource Source,
    string FromSha,
    IReadOnlyList<BucketView> Buckets,
    IReadOnlyList<ReviewThread> Threads,
    IReadOnlyList<string> UnmatchedHunkIds,
    IReadOnlyList<string> UnmatchedFilePaths,
    IReadOnlyList<string> UnmatchedPatternIds,
    Reconciliation Reconciliation);
