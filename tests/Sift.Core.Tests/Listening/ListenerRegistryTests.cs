using Microsoft.Extensions.Time.Testing;
using Sift.Core.Listening;

namespace Sift.Core.Tests.Listening;

public sealed class ListenerRegistryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "sift-listener-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
    private readonly HashSet<int> aliveProcessIds = [100, 200];
    private readonly ListenerRegistry registry;
    private readonly string reviewA;
    private readonly string reviewB;

    public ListenerRegistryTests()
    {
        registry = new ListenerRegistry(Path.Combine(directory, "listeners"), time, aliveProcessIds.Contains);
        reviewA = Path.Combine(directory, "pr-1.json");
        reviewB = Path.Combine(directory, "pr-2.json");
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public void SecondWatcherOnSameReview_IsRefusedAndToldWhoHoldsIt()
    {
        using var first = registry.TryAcquire(reviewA, 100, out _);

        var second = registry.TryAcquire(reviewA, 200, out var holder);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal(100, holder?.ProcessId);
    }

    [Fact]
    public void WatchersOnDifferentReviews_DoNotConflict()
    {
        using var first = registry.TryAcquire(reviewA, 100, out _);
        using var second = registry.TryAcquire(reviewB, 200, out _);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(2, registry.ListLive().Count);
    }

    [Fact]
    public void DeadHolder_IsTakenOver()
    {
        registry.TryAcquire(reviewA, 100, out _);
        aliveProcessIds.Remove(100);

        using var takeover = registry.TryAcquire(reviewA, 200, out _);

        Assert.NotNull(takeover);
        Assert.Equal(200, registry.GetLive(reviewA)?.ProcessId);
    }

    [Fact]
    public void HolderThatStoppedHeartbeating_GoesStaleAndIsTakenOver()
    {
        using var silent = registry.TryAcquire(reviewA, 100, out _);
        time.Advance(ListenerRegistry.StaleAfter + TimeSpan.FromSeconds(1));

        Assert.Null(registry.GetLive(reviewA));
        using var takeover = registry.TryAcquire(reviewA, 200, out _);
        Assert.NotNull(takeover);
    }

    [Fact]
    public void Heartbeat_KeepsListenerLive()
    {
        using var lease = registry.TryAcquire(reviewA, 100, out _)!;

        time.Advance(ListenerRegistry.StaleAfter - TimeSpan.FromSeconds(5));
        Assert.True(lease.Heartbeat());
        time.Advance(ListenerRegistry.StaleAfter - TimeSpan.FromSeconds(5));

        Assert.NotNull(registry.GetLive(reviewA));
    }

    [Fact]
    public void TakenOverLease_StopsHeartbeatingAndLeavesTheNewHolderInPlace()
    {
        var original = registry.TryAcquire(reviewA, 100, out _)!;
        time.Advance(ListenerRegistry.StaleAfter + TimeSpan.FromSeconds(1));
        using var takeover = registry.TryAcquire(reviewA, 200, out _);

        Assert.False(original.Heartbeat());
        original.Dispose();

        Assert.Equal(200, registry.GetLive(reviewA)?.ProcessId);
    }

    [Fact]
    public void Dispose_IsTheStoppedPing()
    {
        var lease = registry.TryAcquire(reviewA, 100, out _)!;

        lease.Dispose();

        Assert.Null(registry.GetLive(reviewA));
        Assert.False(File.Exists(registry.PathFor(reviewA)));
    }
}
