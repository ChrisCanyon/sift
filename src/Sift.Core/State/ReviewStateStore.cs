using System.Collections.Concurrent;
using System.Text.Json;
using Sift.Core.Review;

namespace Sift.Core.State;

public sealed class ThreadNotFoundException(string threadId) : Exception($"Thread not found: {threadId}");

/// <summary>Serializes all changes to a review's state file and writes it atomically.</summary>
public sealed class ReviewStateStore
{
    public const string StateFileSuffix = ".state.json";

    private readonly ConcurrentDictionary<string, SemaphoreSlim> locksByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider timeProvider;

    public ReviewStateStore(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static string StatePathFor(string reviewPath) =>
        Path.ChangeExtension(Path.GetFullPath(reviewPath), null) + StateFileSuffix;

    public async Task<ReviewState> LoadAsync(string reviewPath, CancellationToken cancellationToken = default)
    {
        var statePath = StatePathFor(reviewPath);
        if (!File.Exists(statePath))
        {
            return ReviewState.Empty;
        }

        await using var stream = File.OpenRead(statePath);
        return await JsonSerializer.DeserializeAsync<ReviewState>(stream, SiftJson.Options, cancellationToken) ?? ReviewState.Empty;
    }

    public Task<ReviewState> SetReviewedAsync(string reviewPath, IReadOnlyCollection<string> keys, bool reviewed, CancellationToken cancellationToken = default) =>
        UpdateAsync(reviewPath, state =>
        {
            var reviewedKeys = new HashSet<string>(state.ReviewedKeys, StringComparer.Ordinal);
            if (reviewed)
            {
                reviewedKeys.UnionWith(keys);
            }
            else
            {
                reviewedKeys.ExceptWith(keys);
            }

            return state with { ReviewedKeys = reviewedKeys.Order(StringComparer.Ordinal).ToList() };
        }, cancellationToken);

    public async Task<UserThread> AddThreadAsync(string reviewPath, ThreadKind kind, ThreadAnchor anchor, string body, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var thread = new UserThread(
            Guid.NewGuid().ToString("N")[..10],
            kind,
            ThreadStatus.Open,
            anchor,
            now,
            [new ThreadMessage("you", body, now)]);

        await UpdateAsync(reviewPath, state => state with { Threads = [.. state.Threads, thread] }, cancellationToken);
        return thread;
    }

    /// <summary>A reply from Claude marks the thread answered; a follow-up from the reviewer reopens it.</summary>
    public async Task<UserThread> AddMessageAsync(string reviewPath, string threadId, string author, string body, ThreadStatus? status, CancellationToken cancellationToken = default)
    {
        UserThread? updated = null;
        await UpdateAsync(reviewPath, state =>
        {
            var thread = state.Threads.FirstOrDefault(candidate => candidate.Id == threadId) ?? throw new ThreadNotFoundException(threadId);
            var nextStatus = status ?? (author == "you" ? ThreadStatus.Open : ThreadStatus.Answered);
            updated = thread with
            {
                Status = nextStatus,
                Messages = [.. thread.Messages, new ThreadMessage(author, body, timeProvider.GetUtcNow())],
            };
            return ReplaceThread(state, updated);
        }, cancellationToken);

        return updated!;
    }

    public async Task<UserThread> SetThreadStatusAsync(string reviewPath, string threadId, ThreadStatus status, CancellationToken cancellationToken = default)
    {
        UserThread? updated = null;
        await UpdateAsync(reviewPath, state =>
        {
            var thread = state.Threads.FirstOrDefault(candidate => candidate.Id == threadId) ?? throw new ThreadNotFoundException(threadId);
            updated = thread with { Status = status };
            return ReplaceThread(state, updated);
        }, cancellationToken);

        return updated!;
    }

    /// <summary>Flags unpushed PR comments for Claude to post; returns the flagged threads.</summary>
    public async Task<IReadOnlyList<UserThread>> RequestPushAsync(string reviewPath, IReadOnlyCollection<string>? threadIds, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<UserThread> requested = [];
        await UpdateAsync(reviewPath, state =>
        {
            var threads = state.Threads
                .Select(thread => thread.Kind == ThreadKind.Pr && thread.Status != ThreadStatus.Pushed && (threadIds is null || threadIds.Contains(thread.Id))
                    ? thread with { PushRequested = true }
                    : thread)
                .ToList();
            requested = threads.Where(thread => thread.PushRequested && thread.Status != ThreadStatus.Pushed && (threadIds is null || threadIds.Contains(thread.Id))).ToList();
            return state with { Threads = threads };
        }, cancellationToken);

        return requested;
    }

    public Task<ReviewState> RequestDoneAsync(string reviewPath, CancellationToken cancellationToken = default) =>
        UpdateAsync(reviewPath, state => state with { DoneRequestedAt = timeProvider.GetUtcNow() }, cancellationToken);

    private static ReviewState ReplaceThread(ReviewState state, UserThread updated) =>
        state with { Threads = state.Threads.Select(thread => thread.Id == updated.Id ? updated : thread).ToList() };

    private async Task<ReviewState> UpdateAsync(string reviewPath, Func<ReviewState, ReviewState> update, CancellationToken cancellationToken)
    {
        var statePath = StatePathFor(reviewPath);
        var fileLock = locksByPath.GetOrAdd(statePath, _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync(cancellationToken);
        try
        {
            var updated = update(await LoadAsync(reviewPath, cancellationToken));
            var temporaryPath = statePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(updated, SiftJson.IndentedOptions), cancellationToken);
            File.Move(temporaryPath, statePath, overwrite: true);
            return updated;
        }
        finally
        {
            fileLock.Release();
        }
    }
}
