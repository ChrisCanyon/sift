using Sift.Core.State;

namespace Sift.Core.Tests.State;

public sealed class ReviewStateStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "sift-state-tests", Guid.NewGuid().ToString("N"));
    private readonly string reviewPath;
    private readonly ReviewStateStore store = new();

    private static readonly ThreadAnchor Anchor = new("src/Pay.cs", "h1", null, 42, "new", "total += fee;");

    public ReviewStateStoreTests()
    {
        Directory.CreateDirectory(directory);
        reviewPath = Path.Combine(directory, "pr-1-abc.json");
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public async Task LoadAsync_WithoutStateFile_ReturnsEmptyState()
    {
        var state = await store.LoadAsync(reviewPath);

        Assert.Empty(state.ReviewedKeys);
        Assert.Empty(state.Threads);
        Assert.Equal(Path.Combine(directory, "pr-1-abc.state.json"), ReviewStateStore.StatePathFor(reviewPath));
    }

    [Fact]
    public async Task ConcurrentUpdates_AreAllPersisted()
    {
        var threadTasks = Enumerable.Range(0, 20).Select(index => store.AddThreadAsync(reviewPath, ThreadKind.Ask, Anchor, $"question {index}"));
        var reviewedTasks = Enumerable.Range(0, 20).Select(index => store.SetReviewedAsync(reviewPath, [$"hunk-{index}"], reviewed: true));

        await Task.WhenAll([.. threadTasks, .. reviewedTasks]);

        var reloaded = await new ReviewStateStore().LoadAsync(reviewPath);
        Assert.Equal(20, reloaded.Threads.Count);
        Assert.Equal(20, reloaded.ReviewedKeys.Count);
    }

    [Fact]
    public async Task AddMessageAsync_ClaudeReplyAnswersThreadAndReviewerFollowUpReopensIt()
    {
        var thread = await store.AddThreadAsync(reviewPath, ThreadKind.Ask, Anchor, "Why is the fee added twice?");

        var answered = await store.AddMessageAsync(reviewPath, thread.Id, "claude", "It isn't; the first add is the tax.", status: null);
        Assert.Equal(ThreadStatus.Answered, answered.Status);

        var reopened = await store.AddMessageAsync(reviewPath, thread.Id, "you", "Then rename it.", status: null);
        Assert.Equal(ThreadStatus.Open, reopened.Status);
        Assert.Equal(["you", "claude", "you"], reopened.Messages.Select(message => message.Author));
    }

    [Fact]
    public async Task AddMessageAsync_ExplicitStatus_WinsOverAuthorDefault()
    {
        var thread = await store.AddThreadAsync(reviewPath, ThreadKind.Pr, Anchor, "Needs a null check.");

        var pushed = await store.AddMessageAsync(reviewPath, thread.Id, "claude", "Posted to the PR.", ThreadStatus.Pushed);

        Assert.Equal(ThreadStatus.Pushed, pushed.Status);
    }

    [Fact]
    public async Task AddMessageAsync_UnknownThread_ThrowsWithoutWritingState()
    {
        await Assert.ThrowsAsync<ThreadNotFoundException>(() => store.AddMessageAsync(reviewPath, "missing", "claude", "hi", null));

        Assert.False(File.Exists(ReviewStateStore.StatePathFor(reviewPath)));
    }

    [Fact]
    public async Task SetReviewedAsync_UnmarkingRemovesOnlyTheGivenKeys()
    {
        await store.SetReviewedAsync(reviewPath, ["a", "b", "c"], reviewed: true);

        var state = await store.SetReviewedAsync(reviewPath, ["b"], reviewed: false);

        Assert.Equal(["a", "c"], state.ReviewedKeys);
    }

    [Fact]
    public async Task RequestPushAsync_FlagsOnlyUnpushedPrComments()
    {
        var question = await store.AddThreadAsync(reviewPath, ThreadKind.Ask, Anchor, "Why?");
        var pushed = await store.AddThreadAsync(reviewPath, ThreadKind.Pr, Anchor, "Already posted.");
        await store.SetThreadStatusAsync(reviewPath, pushed.Id, ThreadStatus.Pushed);
        var selected = await store.AddThreadAsync(reviewPath, ThreadKind.Pr, Anchor, "Post this.");
        var unselected = await store.AddThreadAsync(reviewPath, ThreadKind.Pr, Anchor, "Not yet.");

        var requested = await store.RequestPushAsync(reviewPath, [selected.Id, pushed.Id, question.Id]);

        Assert.Equal([selected.Id], requested.Select(thread => thread.Id));
        var reloaded = await new ReviewStateStore().LoadAsync(reviewPath);
        Assert.Equal([selected.Id], reloaded.Threads.Where(thread => thread.PushRequested).Select(thread => thread.Id));
        Assert.DoesNotContain(reloaded.Threads, thread => thread.Id == unselected.Id && thread.PushRequested);
    }
}
