using Sift.Core.Git;

namespace Sift.Core.Review;

public enum Priority
{
    Critical,
    High,
    Normal,
    Low,
}

public sealed record ReviewSource(
    string RepoPath,
    string BaseSha,
    string HeadSha,
    string? BaseRef = null,
    string? HeadRef = null,
    string? PrUrl = null,
    int? PrNumber = null,
    string? CompareUrl = null,
    DiffBase DiffBase = DiffBase.MergeBase);

public sealed record ReviewBucket(string Id, string Title, int Order, bool Collapsed = false, string? Description = null);

public sealed record FileClassification(string Path, string Bucket, Priority? Priority = null, string? Reason = null, string? Risk = null);

public sealed record HunkClassification(string Id, string Bucket, Priority? Priority = null, string? Reason = null, string? Risk = null);

public sealed record PatternClassification(string Id, string Bucket, Priority? Priority = null, string? Reason = null, string? Risk = null);

public sealed record ThreadMessage(string Author, string Body, DateTimeOffset? At = null);

/// <summary>An AI-authored note attached to a hunk when the review is generated.</summary>
public sealed record ReviewThread(string HunkId, int? Line, IReadOnlyList<ThreadMessage> Messages);

/// <summary>Written by the AI; points at a diff by SHA and classifies its files, hunks and patterns.</summary>
public sealed record ReviewDocument(
    int SchemaVersion,
    ReviewSource Source,
    string? Title = null,
    string? Summary = null,
    IReadOnlyList<ReviewBucket>? Buckets = null,
    IReadOnlyList<FileClassification>? Files = null,
    IReadOnlyList<HunkClassification>? Hunks = null,
    IReadOnlyList<PatternClassification>? Patterns = null,
    IReadOnlyList<ReviewThread>? Threads = null);
