using Sift.Core.State;

namespace Sift.Core.Listening;

public sealed record PendingWork(IReadOnlyList<UserThread> Threads, IReadOnlyList<UserThread> PushComments, bool DoneRequested)
{
    public bool IsEmpty => Threads.Count == 0 && PushComments.Count == 0 && !DoneRequested;
}

/// <summary>
/// Derives what Claude still owes a review from its saved state, so a restarted watcher replays
/// everything outstanding instead of depending on having seen the original event.
/// </summary>
public sealed class PendingWorkTracker(DateTimeOffset startedAt)
{
    private readonly HashSet<string> emittedThreadVersions = new(StringComparer.Ordinal);
    private readonly HashSet<string> emittedPushThreadIds = new(StringComparer.Ordinal);
    private DateTimeOffset lastDoneRequestedAt = startedAt;

    public static bool AwaitsClaude(UserThread thread) =>
        thread.Kind != ThreadKind.Pr && thread.Status == ThreadStatus.Open && thread.Messages.Count > 0 && thread.Messages[^1].Author == "you";

    public static bool AwaitsPush(UserThread thread) =>
        thread.Kind == ThreadKind.Pr && thread.PushRequested && thread.Status != ThreadStatus.Pushed;

    /// <summary>Work not yet reported by this tracker. A thread is reported again only after a new reviewer message.</summary>
    public PendingWork TakeNew(ReviewState state)
    {
        var threads = state.Threads
            .Where(AwaitsClaude)
            .Where(thread => emittedThreadVersions.Add($"{thread.Id}:{thread.Messages.Count}"))
            .ToList();

        var pushComments = state.Threads
            .Where(AwaitsPush)
            .Where(thread => emittedPushThreadIds.Add(thread.Id))
            .ToList();

        // A done request made while nobody listened is stale; only one made during this watch counts.
        var doneRequested = state.DoneRequestedAt is { } doneAt && doneAt > lastDoneRequestedAt;
        if (doneRequested)
        {
            lastDoneRequestedAt = state.DoneRequestedAt!.Value;
        }

        return new PendingWork(threads, pushComments, doneRequested);
    }
}
