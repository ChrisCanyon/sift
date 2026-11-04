using System.Text.Json;
using System.Threading.Channels;
using Sift.Core;

namespace Sift;

/// <summary>UI-facing notifications to connected browsers over server-sent events.</summary>
public sealed class EventHub
{
    private readonly List<Channel<string>> browserSubscribers = [];

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
}

/// <summary>One JSON line per event for Claude; the event name comes first so a grep or a glance finds it.</summary>
public static class AiEvent
{
    public static string Format(string eventName, object payload)
    {
        var node = JsonSerializer.SerializeToNode(payload, SiftJson.FileOptions)!.AsObject();
        node.Insert(0, "event", eventName);
        node.Insert(1, "at", DateTimeOffset.UtcNow);
        return node.ToJsonString(SiftJson.FileOptions);
    }
}
