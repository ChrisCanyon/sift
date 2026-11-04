using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Sift.Core;
using Sift.Core.Git;
using Sift.Core.Listening;
using Sift.Core.Review;
using Sift.Core.State;

namespace Sift;

public sealed record ServerInfo(int Port, int ProcessId, string Url);

public sealed record ReviewedRequest(string Review, IReadOnlyList<string> Keys, bool Reviewed);

public sealed record NewThreadRequest(string Review, ThreadKind Kind, ThreadAnchor Anchor, string Body);

public sealed record MessageRequest(string Review, string Author, string Body, ThreadStatus? Status = null);

public sealed record ThreadStatusRequest(string Review, ThreadStatus Status);

public sealed record ReviewActionRequest(string Review, IReadOnlyList<string>? ThreadIds = null);

public static class ServerCommand
{
    public const string RequestHeaderName = "X-Sift";

    public static async Task<int> RunAsync(string[] commandArguments)
    {
        var options = CommandLineOptions.Parse(commandArguments);
        var reviewStore = new ReviewStore(SiftPaths.ReviewsPath);
        var stateStore = new ReviewStateStore();
        var eventHub = new EventHub();
        var listenerRegistry = new ListenerRegistry(SiftPaths.ListenersPath);

        string? sourcePath = null;
        if (options.TryGetValue("source", out var sourceArgument))
        {
            sourcePath = Path.GetFullPath(sourceArgument);
            if (!File.Exists(sourcePath))
            {
                Console.Error.WriteLine($"Review file not found: {sourcePath}");
                return 2;
            }

            reviewStore.Register(sourcePath);
        }

        var port = options.TryGetValue("port", out var portArgument) ? int.Parse(portArgument) : 0;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = ResolveWebRoot(),
        });
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1";
        // Stdout carries only the JSON ready line.
        builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.ConfigureHttpJsonOptions(json => SiftJson.Configure(json.SerializerOptions));

        var app = builder.Build();
        var views = new ReviewViewCache(reviewStore);
        var fileLines = new FileLinesCache();

        app.Use(RejectCrossSiteWrites);
        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapGet("/api/reviews", () => new { current = sourcePath, reviews = reviewStore.List(maximumCount: 25) });

        app.MapGet("/api/review", async (string? path, CancellationToken cancellationToken) =>
        {
            var reviewPath = path is { Length: > 0 } ? path : sourcePath;
            if (reviewPath is null)
            {
                return Results.NotFound(new { error = "No review selected." });
            }

            var result = await views.GetAsync(reviewPath, cancellationToken);
            return result.View is not null ? Results.Ok(result.View) : Results.UnprocessableEntity(new { error = result.Error });
        });

        app.MapGet("/api/state", async (string path, CancellationToken cancellationToken) =>
            reviewStore.IsKnown(path) ? Results.Ok(await stateStore.LoadAsync(path, cancellationToken)) : UnknownReview(path));

        app.MapPost("/api/state/reviewed", async (ReviewedRequest request, CancellationToken cancellationToken) =>
        {
            if (!reviewStore.IsKnown(request.Review))
            {
                return UnknownReview(request.Review);
            }

            var state = await stateStore.SetReviewedAsync(request.Review, request.Keys, request.Reviewed, cancellationToken);
            eventHub.PublishToBrowsers("state-changed", new { review = request.Review });
            return Results.Ok(state);
        });

        app.MapPost("/api/threads", async (NewThreadRequest request, CancellationToken cancellationToken) =>
        {
            if (!reviewStore.IsKnown(request.Review))
            {
                return UnknownReview(request.Review);
            }

            if (string.IsNullOrWhiteSpace(request.Body))
            {
                return Results.BadRequest(new { error = "Message is empty." });
            }

            var thread = await stateStore.AddThreadAsync(request.Review, request.Kind, request.Anchor, request.Body.Trim(), cancellationToken);
            eventHub.PublishToBrowsers("state-changed", new { review = request.Review });
            return Results.Ok(thread);
        });

        app.MapPost("/api/threads/{threadId}/messages", async (string threadId, MessageRequest request, CancellationToken cancellationToken) =>
        {
            if (!reviewStore.IsKnown(request.Review))
            {
                return UnknownReview(request.Review);
            }

            try
            {
                var thread = await stateStore.AddMessageAsync(request.Review, threadId, request.Author, request.Body.Trim(), request.Status, cancellationToken);
                eventHub.PublishToBrowsers("state-changed", new { review = request.Review });
                return Results.Ok(thread);
            }
            catch (ThreadNotFoundException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
        });

        app.MapPost("/api/threads/{threadId}/status", async (string threadId, ThreadStatusRequest request, CancellationToken cancellationToken) =>
        {
            if (!reviewStore.IsKnown(request.Review))
            {
                return UnknownReview(request.Review);
            }

            try
            {
                var thread = await stateStore.SetThreadStatusAsync(request.Review, threadId, request.Status, cancellationToken);
                eventHub.PublishToBrowsers("state-changed", new { review = request.Review });
                return Results.Ok(thread);
            }
            catch (ThreadNotFoundException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
        });

        app.MapPost("/api/push", async (ReviewActionRequest request, CancellationToken cancellationToken) =>
        {
            if (!reviewStore.IsKnown(request.Review))
            {
                return UnknownReview(request.Review);
            }

            var requested = await stateStore.RequestPushAsync(request.Review, request.ThreadIds, cancellationToken);
            if (requested.Count == 0)
            {
                return Results.BadRequest(new { error = "No unpushed PR comments." });
            }

            eventHub.PublishToBrowsers("state-changed", new { review = request.Review });
            return Results.Ok(new { queued = requested.Count });
        });

        app.MapPost("/api/done", async (ReviewActionRequest request, CancellationToken cancellationToken) =>
        {
            if (!reviewStore.IsKnown(request.Review))
            {
                return UnknownReview(request.Review);
            }

            await stateStore.RequestDoneAsync(request.Review, cancellationToken);
            return Results.Ok();
        });

        app.MapGet("/api/file-lines", async (string review, string path, string side, int start, int end, CancellationToken cancellationToken) =>
        {
            var view = (await views.GetAsync(review, cancellationToken)).View;
            if (view is null)
            {
                return UnknownReview(review);
            }

            var commitSha = side == "old" ? view.FromSha : view.Source.HeadSha;
            try
            {
                var lines = await fileLines.GetAsync(view.Source.RepoPath, commitSha, path, cancellationToken);
                var firstIndex = Math.Clamp(start, 1, lines.Count + 1) - 1;
                var lastIndex = Math.Clamp(end, 0, lines.Count);
                return Results.Ok(new
                {
                    totalLines = lines.Count,
                    lines = lines.Skip(firstIndex).Take(Math.Max(0, lastIndex - firstIndex))
                        .Select((text, offset) => new { number = firstIndex + offset + 1, text }),
                });
            }
            catch (GitCommandException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
        });

        app.MapGet("/api/listeners", () => new { reviews = LiveListenerReviews(listenerRegistry) });

        app.MapGet("/api/events", (HttpContext context) => eventHub.StreamToBrowserAsync(context.Response, context.RequestAborted));

        app.MapFallbackToFile("index.html");

        using var reviewWatcher = WatchReviewFiles(reviewStore.RootPath, sourcePath, eventHub);
        _ = PublishListenerChangesAsync(listenerRegistry, eventHub, app.Lifetime.ApplicationStopping);

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            var serverInfo = new ServerInfo(new Uri(address).Port, Environment.ProcessId, address);
            File.WriteAllText(SiftPaths.ServerInfoPath, JsonSerializer.Serialize(serverInfo, SiftJson.Options));
            Console.Out.WriteLine(AiEvent.Format("ready", new { url = address, source = sourcePath }));

            if (!options.ContainsKey("no-open"))
            {
                var startUrl = sourcePath is null ? address : $"{address}/?review={Uri.EscapeDataString(sourcePath)}";
                Process.Start(new ProcessStartInfo(startUrl) { UseShellExecute = true });
            }
        });

        app.Lifetime.ApplicationStopping.Register(DeleteServerInfoIfOwned);

        await app.RunAsync();
        return 0;
    }

    /// <summary>
    /// Writes reach an agent that edits code, so a page on another origin must not be able to forge them.
    /// Requiring a custom header forces a CORS preflight, which this server never approves.
    /// </summary>
    internal static async Task RejectCrossSiteWrites(HttpContext context, Func<Task> next)
    {
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            var origin = request.Headers.Origin.ToString();
            var isSameOrigin = origin.Length == 0 || origin == $"{request.Scheme}://{request.Host}";
            var isJson = request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true;
            if (!isSameOrigin || !isJson || !request.Headers.ContainsKey(RequestHeaderName))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }

        await next();
    }

    private static IResult UnknownReview(string reviewPath) => Results.NotFound(new { error = $"Unknown review: {reviewPath}" });

    private static IReadOnlyList<string> LiveListenerReviews(ListenerRegistry registry) =>
        registry.ListLive().Select(listener => listener.Review).Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Polls rather than watching files: a killed watcher changes no file, it just stops heartbeating.</summary>
    private static async Task PublishListenerChangesAsync(ListenerRegistry registry, EventHub eventHub, CancellationToken stopping)
    {
        IReadOnlyList<string> published = [];
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(stopping))
            {
                var live = LiveListenerReviews(registry);
                if (!live.SequenceEqual(published, StringComparer.OrdinalIgnoreCase))
                {
                    published = live;
                    eventHub.PublishToBrowsers("listeners-changed", new { reviews = live });
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static FileSystemWatcher? WatchReviewFiles(string reviewsRoot, string? sourcePath, EventHub eventHub)
    {
        var watchRoot = sourcePath is not null && !sourcePath.StartsWith(reviewsRoot, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(sourcePath)!
            : reviewsRoot;
        Directory.CreateDirectory(watchRoot);

        var watcher = new FileSystemWatcher(watchRoot, "*.json") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
        var pendingNotifications = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.OrdinalIgnoreCase);

        void OnChanged(object sender, FileSystemEventArgs change)
        {
            if (change.FullPath.EndsWith(ReviewStateStore.StateFileSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Editors and the AI write in bursts; notify once the file settles.
            var debounce = new CancellationTokenSource();
            pendingNotifications.AddOrUpdate(change.FullPath, debounce, (_, previous) =>
            {
                previous.Cancel();
                return debounce;
            });

            _ = Task.Delay(TimeSpan.FromMilliseconds(400), debounce.Token).ContinueWith(task =>
            {
                if (!task.IsCanceled)
                {
                    eventHub.PublishToBrowsers("review-changed", new { review = change.FullPath });
                }
            }, TaskScheduler.Default);
        }

        watcher.Changed += OnChanged;
        watcher.Created += OnChanged;
        watcher.Renamed += OnChanged;
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private static void DeleteServerInfoIfOwned()
    {
        try
        {
            var serverInfo = JsonSerializer.Deserialize<ServerInfo>(File.ReadAllText(SiftPaths.ServerInfoPath), SiftJson.Options);
            if (serverInfo?.ProcessId == Environment.ProcessId)
            {
                File.Delete(SiftPaths.ServerInfoPath);
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
        }
    }

    private static string ResolveWebRoot() => Path.Combine(AppContext.BaseDirectory, "ui");
}

public sealed record ReviewViewResult(ReviewView? View, string? Error);

/// <summary>File contents by commit; immutable, so entries never go stale.</summary>
public sealed class FileLinesCache
{
    private const int MaximumCachedFiles = 200;

    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> linesByKey = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<string>> GetAsync(string repositoryPath, string commitSha, string path, CancellationToken cancellationToken)
    {
        var key = $"{repositoryPath}\0{commitSha}\0{path}";
        if (linesByKey.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var lines = await new GitRepository(repositoryPath).GetFileLinesAsync(commitSha, path, cancellationToken);
        if (linesByKey.Count >= MaximumCachedFiles)
        {
            linesByKey.Clear();
        }

        linesByKey[key] = lines;
        return lines;
    }
}

/// <summary>Assembled views keyed by review path, rebuilt when the review file changes.</summary>
public sealed class ReviewViewCache(ReviewStore reviewStore)
{
    private readonly ConcurrentDictionary<string, (DateTime Modified, ReviewView View)> viewsByPath = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ReviewViewResult> GetAsync(string reviewPath, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(reviewPath);
        if (!reviewStore.IsKnown(fullPath) || !File.Exists(fullPath))
        {
            return new ReviewViewResult(null, $"Unknown review: {fullPath}");
        }

        var modified = File.GetLastWriteTimeUtc(fullPath);
        if (viewsByPath.TryGetValue(fullPath, out var cached) && cached.Modified == modified)
        {
            return new ReviewViewResult(cached.View, null);
        }

        try
        {
            var document = await reviewStore.LoadAsync(fullPath, cancellationToken);
            var diff = await new GitRepository(document.Source.RepoPath)
                .GetDiffAsync(document.Source.BaseSha, document.Source.HeadSha, document.Source.DiffBase, cancellationToken);
            var view = ReviewAssembler.Assemble(document, diff);
            viewsByPath[fullPath] = (modified, view);
            return new ReviewViewResult(view, null);
        }
        catch (Exception exception) when (exception is GitCommandException or InvalidDataException or JsonException or IOException)
        {
            return new ReviewViewResult(null, exception.Message);
        }
    }
}
