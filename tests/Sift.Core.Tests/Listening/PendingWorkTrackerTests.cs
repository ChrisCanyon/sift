using Sift.Core.Listening;
using Sift.Core.Review;
using Sift.Core.State;

namespace Sift.Core.Tests.Listening;

public class PendingWorkTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly ThreadAnchor Anchor = new("src/Pay.cs", "h1", null, 42, "new", "total += fee;");

    private static UserThread Thread(string id, ThreadKind kind, ThreadStatus status, params string[] authors) =>
        new(id, kind, status, Anchor, Start, authors.Select(author => new ThreadMessage(author, $"{author} says", Start)).ToList());

    private static ReviewState State(params UserThread[] threads) => new([], threads);

    [Fact]
    public void FirstPass_ReplaysOnlyWorkStillOwedToClaude()
    {
        var tracker = new PendingWorkTracker(Start);

        var work = tracker.TakeNew(State(
            Thread("unanswered", ThreadKind.Ask, ThreadStatus.Open, "you"),
            Thread("answered", ThreadKind.Ask, ThreadStatus.Answered, "you", "claude"),
            Thread("followed-up", ThreadKind.Change, ThreadStatus.Open, "you", "claude", "you"),
            Thread("draft-comment", ThreadKind.Pr, ThreadStatus.Open, "you"),
            Thread("push-wanted", ThreadKind.Pr, ThreadStatus.Open, "you") with { PushRequested = true },
            Thread("already-pushed", ThreadKind.Pr, ThreadStatus.Pushed, "you") with { PushRequested = true }));

        Assert.Equal(["unanswered", "followed-up"], work.Threads.Select(thread => thread.Id));
        Assert.Equal(["push-wanted"], work.PushComments.Select(thread => thread.Id));
        Assert.False(work.DoneRequested);
    }

    [Fact]
    public void ThreadIsReportedOncePerReviewerMessage()
    {
        var tracker = new PendingWorkTracker(Start);
        var asked = Thread("t1", ThreadKind.Ask, ThreadStatus.Open, "you");

        Assert.Single(tracker.TakeNew(State(asked)).Threads);
        Assert.Empty(tracker.TakeNew(State(asked)).Threads);

        var followUp = Thread("t1", ThreadKind.Ask, ThreadStatus.Open, "you", "claude", "you");
        Assert.Single(tracker.TakeNew(State(followUp)).Threads);
    }

    [Fact]
    public void SecondPushReportsOnlyNewlyRequestedComments()
    {
        var tracker = new PendingWorkTracker(Start);
        var first = Thread("c1", ThreadKind.Pr, ThreadStatus.Open, "you") with { PushRequested = true };
        tracker.TakeNew(State(first));

        var second = Thread("c2", ThreadKind.Pr, ThreadStatus.Open, "you") with { PushRequested = true };
        var work = tracker.TakeNew(State(first, second));

        Assert.Equal(["c2"], work.PushComments.Select(thread => thread.Id));
    }

    [Fact]
    public void DoneRequestedBeforeTheWatchStarted_IsNotReplayed()
    {
        var tracker = new PendingWorkTracker(Start);

        Assert.False(tracker.TakeNew(State() with { DoneRequestedAt = Start.AddMinutes(-5) }).DoneRequested);

        var during = State() with { DoneRequestedAt = Start.AddMinutes(1) };
        Assert.True(tracker.TakeNew(during).DoneRequested);
        Assert.False(tracker.TakeNew(during).DoneRequested);
    }
}
