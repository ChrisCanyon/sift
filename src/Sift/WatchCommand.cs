using System.Text.Json;
using Sift.Core;
using Sift.Core.Git;
using Sift.Core.Listening;
using Sift.Core.Review;
using Sift.Core.State;

namespace Sift;

/// <summary>Claude's listener for one review: replays outstanding work, then streams new work as JSON lines on stdout.</summary>
public static class WatchCommand
{
    public const int AlreadyListeningExitCode = 4;

    private const string Usage = "Usage: sift watch --review <file> [--max-minutes <n>]";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public static async Task<int> RunAsync(string[] commandArguments)
    {
        var options = CommandLineOptions.Parse(commandArguments);
        if (!options.TryGetValue("review", out var reviewArgument))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var reviewPath = Path.GetFullPath(reviewArgument);
        if (!File.Exists(reviewPath))
        {
            Console.Error.WriteLine($"Review file not found: {reviewPath}");
            return 2;
        }

        TimeSpan? maximumDuration = null;
        if (options.TryGetValue("max-minutes", out var minutesArgument))
        {
            if (!double.TryParse(minutesArgument, out var minutes) || minutes <= 0)
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }

            maximumDuration = TimeSpan.FromMinutes(minutes);
        }

        var registry = new ListenerRegistry(SiftPaths.ListenersPath);
        using var lease = registry.TryAcquire(reviewPath, Environment.ProcessId, out var holder);
        if (lease is null)
        {
            Console.Error.WriteLine(holder is null
                ? $"Another Claude is starting to listen on {reviewPath}."
                : $"Claude (pid {holder.ProcessId}) is already listening on {reviewPath} since {holder.StartedAt:u}. Use that session, or stop it first.");
            return AlreadyListeningExitCode;
        }

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, cancelEvent) =>
        {
            cancelEvent.Cancel = true;
            stopping.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => lease.Dispose();

        var stateStore = new ReviewStateStore();
        var context = new ReviewContextCache(reviewPath);
        var tracker = new PendingWorkTracker(lease.Info.StartedAt);
        var rearmCommand = $"sift watch --review \"{reviewPath}\"" + (maximumDuration is null ? "" : $" --max-minutes {minutesArgument}");

        WriteEvent("listening", new { review = reviewPath, processId = Environment.ProcessId, rearmCommand });

        var deadline = maximumDuration is null ? (DateTimeOffset?)null : DateTimeOffset.UtcNow + maximumDuration.Value;
        var nextHeartbeat = DateTimeOffset.UtcNow + ListenerRegistry.HeartbeatInterval;
        DateTime? lastStateWrite = null;
        var isFirstPass = true;

        while (!stopping.IsCancellationRequested)
        {
            var statePath = ReviewStateStore.StatePathFor(reviewPath);
            var stateWrite = File.Exists(statePath) ? File.GetLastWriteTimeUtc(statePath) : DateTime.MinValue;
            if (isFirstPass || stateWrite != lastStateWrite)
            {
                try
                {
                    var work = tracker.TakeNew(await stateStore.LoadAsync(reviewPath, stopping.Token));
                    await EmitAsync(work, context, replayed: isFirstPass, stopping.Token);
                    lastStateWrite = stateWrite;
                    isFirstPass = false;
                }
                catch (Exception exception) when (exception is IOException or JsonException)
                {
                    // Mid-write; the next poll retries.
                }
            }

            var now = DateTimeOffset.UtcNow;
            if (now >= nextHeartbeat)
            {
                if (!lease.Heartbeat())
                {
                    WriteEvent("watch-replaced", new { review = reviewPath, message = "Another Claude took over this review; this watch has stopped." });
                    return 0;
                }

                nextHeartbeat = now + ListenerRegistry.HeartbeatInterval;
            }

            if (now >= deadline)
            {
                WriteEvent("watch-expired", new { review = reviewPath, message = "Watch hit --max-minutes. Re-arm now to keep listening.", rearmCommand });
                return 0;
            }

            try
            {
                await Task.Delay(PollInterval, stopping.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        return 0;
    }

    private static async Task EmitAsync(PendingWork work, ReviewContextCache context, bool replayed, CancellationToken cancellationToken)
    {
        if (work.IsEmpty)
        {
            return;
        }

        var source = await context.GetAsync(cancellationToken);
        foreach (var thread in work.Threads)
        {
            WriteEvent(thread.Messages.Count == 1 ? "thread" : "thread-reply", new
            {
                review = context.ReviewPath,
                replayed,
                threadId = thread.Id,
                kind = thread.Kind,
                message = thread.Messages[^1].Body,
                anchor = thread.Anchor,
                repo = source?.Document.Source.RepoPath,
                prUrl = source?.Document.Source.PrUrl,
                headSha = source?.Document.Source.HeadSha,
                diffCommand = source?.FromSha is null
                    ? null
                    : $"git -C \"{source.Document.Source.RepoPath}\" diff {source.FromSha} {source.Document.Source.HeadSha} -- \"{thread.Anchor.FilePath}\"",
            });
        }

        if (work.PushComments.Count > 0)
        {
            WriteEvent("push-to-pr", new
            {
                review = context.ReviewPath,
                replayed,
                prUrl = source?.Document.Source.PrUrl,
                prNumber = source?.Document.Source.PrNumber,
                headSha = source?.Document.Source.HeadSha,
                comments = work.PushComments.Select(thread => new
                {
                    threadId = thread.Id,
                    path = thread.Anchor.FilePath,
                    line = thread.Anchor.Line,
                    side = thread.Anchor.Side == "old" ? "LEFT" : "RIGHT",
                    body = string.Join("\n\n", thread.Messages.Where(message => message.Author == "you").Select(message => message.Body)),
                }),
            });
        }

        if (work.DoneRequested)
        {
            var state = await new ReviewStateStore().LoadAsync(context.ReviewPath, cancellationToken);
            WriteEvent("review-done", new
            {
                review = context.ReviewPath,
                reviewedCount = state.ReviewedKeys.Count,
                openThreads = state.Threads.Count(thread => thread.Status == ThreadStatus.Open),
                unpushedPrComments = state.Threads.Count(thread => thread.Kind == ThreadKind.Pr && thread.Status != ThreadStatus.Pushed),
            });
        }
    }

    private static void WriteEvent(string eventName, object payload) => Console.Out.WriteLine(AiEvent.Format(eventName, payload));

    private sealed record ReviewContext(ReviewDocument Document, string? FromSha);

    /// <summary>The review's source, reloaded only when Claude rewrites the review file.</summary>
    private sealed class ReviewContextCache(string reviewPath)
    {
        private DateTime loadedWrite;
        private ReviewContext? context;

        public string ReviewPath { get; } = reviewPath;

        public async Task<ReviewContext?> GetAsync(CancellationToken cancellationToken)
        {
            var write = File.GetLastWriteTimeUtc(ReviewPath);
            if (context is not null && write == loadedWrite)
            {
                return context;
            }

            try
            {
                var document = await new ReviewStore(SiftPaths.ReviewsPath).LoadAsync(ReviewPath, cancellationToken);
                var source = document.Source;
                string? fromSha;
                try
                {
                    fromSha = source.DiffBase == DiffBase.Direct
                        ? source.BaseSha
                        : await new GitRepository(source.RepoPath).GetMergeBaseAsync(source.BaseSha, source.HeadSha, cancellationToken);
                }
                catch (GitCommandException)
                {
                    fromSha = null;
                }

                context = new ReviewContext(document, fromSha);
                loadedWrite = write;
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
            {
                // Keep the last good context; events still carry the thread itself.
            }

            return context;
        }
    }
}
