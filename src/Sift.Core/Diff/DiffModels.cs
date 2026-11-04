using Sift.Core.Git;

namespace Sift.Core.Diff;

public enum FileChangeKind
{
    Modified,
    Added,
    Deleted,
    Renamed,
    Copied,
}

public enum DiffLineKind
{
    Context,
    Added,
    Removed,
}

public sealed record DiffLine(
    DiffLineKind Kind,
    string Text,
    int? OldLineNumber,
    int? NewLineNumber,
    bool NoNewlineAtEnd = false);

public sealed record DiffHunk(
    string Id,
    int OldStart,
    int OldLineCount,
    int NewStart,
    int NewLineCount,
    string SectionHeader,
    IReadOnlyList<DiffLine> Lines);

public sealed record DiffFile(
    string Path,
    string? OldPath,
    FileChangeKind ChangeKind,
    bool IsBinary,
    int? SimilarityPercent,
    string? OldMode,
    string? NewMode,
    IReadOnlyList<DiffHunk> Hunks)
{
    public int AddedLineCount => Hunks.Sum(hunk => hunk.Lines.Count(line => line.Kind == DiffLineKind.Added));
    public int RemovedLineCount => Hunks.Sum(hunk => hunk.Lines.Count(line => line.Kind == DiffLineKind.Removed));

    /// <summary>Changed lines whose only difference was CRLF vs LF; left out of the hunks.</summary>
    public int LineEndingChangedLines { get; init; }
}

/// <summary>FromSha is the merge base, or BaseSha itself for a direct diff.</summary>
public sealed record DiffResult(string BaseSha, string HeadSha, string FromSha, DiffBase DiffBase, IReadOnlyList<DiffFile> Files)
{
    /// <summary>Null when the diff wasn't read from git (e.g. built in a test).</summary>
    public Reconciliation? Reconciliation { get; init; }
}
