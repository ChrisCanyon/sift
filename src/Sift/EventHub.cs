using System.Text.Json;
using System.Threading.Channels;
using Sift.Core;

namespace Sift;

/// <summary>
/// AI-facing events go to stdout and the append-only events log; UI-facing notifications go to
/// connected browsers over server-sent events.
/// </summary>
public sealed class EventHub
{
    private readonly Lock writeLock = new();
    private readonly List<Channel<string>> browserSubscribers = [];
    private readonly string eventsPath;

    public EventHub(string eventsPath)
    {
        this.eventsPath = eventsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(eventsPath)!);
    }

    public void PublishToAi(string eventName, object payload)
    {
        var line = SerializeEvent(eventName, payload);
        lock (writeLock)
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
            File.AppendAllText(eventsPath, line + "\n");
        }
    }

    public void PublishToBrowsers(string eventName, object payload)
    {
        var message = $"event: {eventName}\ndata: {JsonSerializer.Serialize(payload, SiftJson.Options)}\n\n";
        lock (browserSubscribers)
        {
            foreach (var subscriber in browserSubscribers)
            {
                subscriber.Writer.TryWrite(message);
            }
        }
    }

    public async Task StreamToBrowserAsync(HttpResponse response, CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (browserSubscribers)
        {
            browserSubscribers.Add(channel);
        }

        try
        {
            response.Headers.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache";
            await response.WriteAsync(": connected\n\n", cancellationToken);
            await response.Body.FlushAsync(cancellationToken);

            await foreach (var message in channel.Reader.ReadAllAsync(cancellationToken))
            {
                await response.WriteAsync(message, cancellationToken);
                await response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (browserSubscribers)
            {
                browserSubscribers.Remove(channel);
            }
        }
    }

    private static string SerializeEvent(string eventName, object payload)
    {
        var node = JsonSerializer.SerializeToNode(payload, SiftJson.FileOptions)!.AsObject();
        node.Insert(0, "event", eventName);
        node.Insert(1, "at", DateTimeOffset.UtcNow);
        return node.ToJsonString(SiftJson.FileOptions);
    }
}
