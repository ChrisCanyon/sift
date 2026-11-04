using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sift.Core.Listening;

public sealed record ListenerInfo(string Review, int ProcessId, DateTimeOffset StartedAt, DateTimeOffset HeartbeatAt);

/// <summary>
/// One file per review under ~/.sift/listeners marks a live Claude watcher. Creating it is the "listening" ping,
/// heartbeats keep it fresh, deleting it is the "stopped" ping. A watcher killed outright leaves a stale file,
/// which the heartbeat age exposes.
/// </summary>
public sealed class ListenerRegistry
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(35);

    private readonly TimeProvider timeProvider;
    private readonly Func<int, bool> isProcessAlive;

    public ListenerRegistry(string listenersPath, TimeProvider? timeProvider = null, Func<int, bool>? isProcessAlive = null)
    {
        ListenersPath = Path.GetFullPath(listenersPath);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.isProcessAlive = isProcessAlive ?? IsProcessAlive;
    }

    public string ListenersPath { get; }

    public string PathFor(string reviewPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(reviewPath).ToUpperInvariant()));
        return Path.Combine(ListenersPath, Convert.ToHexStringLower(hash)[..16] + ".json");
    }

    /// <summary>Claims the review for this process, taking over from a dead or silent holder. Null when a live listener holds it.</summary>
    public ListenerLease? TryAcquire(string reviewPath, int processId, out ListenerInfo? holder)
    {
        Directory.CreateDirectory(ListenersPath);
        var listenerPath = PathFor(reviewPath);
        var now = timeProvider.GetUtcNow();
        var info = new ListenerInfo(Path.GetFullPath(reviewPath), processId, now, now);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using (var stream = new FileStream(listenerPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    JsonSerializer.Serialize(stream, info, SiftJson.IndentedOptions);
                }

                holder = null;
                return new ListenerLease(this, listenerPath, info);
            }
            catch (IOException) when (File.Exists(listenerPath))
            {
                var existing = Read(listenerPath);
                if (existing is null ? IsRecentlyWritten(listenerPath, now) : IsLive(existing))
                {
                    holder = existing;
                    return null;
                }

                TryDelete(listenerPath);
            }
        }

        holder = Read(listenerPath);
        return null;
    }

    public ListenerInfo? GetLive(string reviewPath)
    {
        var info = Read(PathFor(reviewPath));
        return info is not null && IsLive(info) ? info : null;
    }

    public IReadOnlyList<ListenerInfo> ListLive() =>
        Directory.Exists(ListenersPath)
            ? Directory.EnumerateFiles(ListenersPath, "*.json").Select(Read).OfType<ListenerInfo>().Where(IsLive).ToList()
            : [];

    public bool IsLive(ListenerInfo info) =>
        timeProvider.GetUtcNow() - info.HeartbeatAt < StaleAfter && isProcessAlive(info.ProcessId);

    internal DateTimeOffset Now => timeProvider.GetUtcNow();

    internal static ListenerInfo? Read(string listenerPath)
    {
        try
        {
            return JsonSerializer.Deserialize<ListenerInfo>(File.ReadAllText(listenerPath), SiftJson.Options);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static void TryDelete(string listenerPath)
    {
        try
        {
            File.Delete(listenerPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    // A half-written file from a racing acquirer must not be taken over.
    private bool IsRecentlyWritten(string listenerPath, DateTimeOffset now) =>
        File.Exists(listenerPath) && now - new DateTimeOffset(File.GetLastWriteTimeUtc(listenerPath), TimeSpan.Zero) < StaleAfter;

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}

public sealed class ListenerLease : IDisposable
{
    private readonly ListenerRegistry registry;
    private readonly string listenerPath;
    private ListenerInfo info;
    private bool released;

    internal ListenerLease(ListenerRegistry registry, string listenerPath, ListenerInfo info)
    {
        this.registry = registry;
        this.listenerPath = listenerPath;
        this.info = info;
    }

    public ListenerInfo Info => info;

    /// <summary>False when another watcher has taken the review over; the caller should stop.</summary>
    public bool Heartbeat()
    {
        if (!IsStillOwned())
        {
            return false;
        }

        info = info with { HeartbeatAt = registry.Now };
        var temporaryPath = listenerPath + $".{info.ProcessId}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(info, SiftJson.IndentedOptions));
        File.Move(temporaryPath, listenerPath, overwrite: true);
        return true;
    }

    public void Dispose()
    {
        if (released)
        {
            return;
        }

        released = true;
        if (IsStillOwned())
        {
            ListenerRegistry.TryDelete(listenerPath);
        }
    }

    // An unreadable file (mid-replace, or deleted) still counts as ours; only a different holder ends the lease.
    private bool IsStillOwned() =>
        ListenerRegistry.Read(listenerPath) is not { } current || current.ProcessId == info.ProcessId && current.StartedAt == info.StartedAt;
}
