using Sift.Core.Review;

namespace Sift.Core.State;

public enum ThreadKind
{
    /// <summary>A question for Claude about the change.</summary>
    Ask,

    /// <summary>Ask Claude to edit the code.</summary>
    Change,

    /// <summary>A comment to post on the pull request.</summary>
    Pr,
}

public enum ThreadStatus
{
    Open,
    Answered,
    Done,
    Pushed,
}

public sealed record ThreadAnchor(
    string FilePath,
    string? HunkId,
    string? PatternId,
    int? Line,
    string? Side,
    string? LineText);

public sealed record UserThread(
    string Id,
    ThreadKind Kind,
    ThreadStatus Status,
    ThreadAnchor Anchor,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ThreadMessage> Messages,
    bool PushRequested = false);

/// <summary>The reviewer's progress and conversations for one review. Owned by the app, never by the AI.</summary>
public sealed record ReviewState(IReadOnlyList<string> ReviewedKeys, IReadOnlyList<UserThread> Threads, DateTimeOffset? DoneRequestedAt = null)
{
    public static readonly ReviewState Empty = new([], []);
}
