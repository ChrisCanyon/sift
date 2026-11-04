namespace Sift.Core;

public static class SiftPaths
{
    public static string HomePath =>
        Environment.GetEnvironmentVariable("SIFT_HOME") is { Length: > 0 } siftHome
            ? siftHome
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sift");

    public static string ReviewsPath => Path.Combine(HomePath, "reviews");

    /// <summary>Append-only JSON lines of UI events, tailed by the AI.</summary>
    public static string EventsPath => Path.Combine(HomePath, "events.jsonl");

    /// <summary>Port and pid of the running server, for CLI commands.</summary>
    public static string ServerInfoPath => Path.Combine(HomePath, "server.json");
}
