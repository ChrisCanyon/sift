namespace Sift.Core;

public static class SiftPaths
{
    public static string HomePath =>
        Environment.GetEnvironmentVariable("SIFT_HOME") is { Length: > 0 } siftHome
            ? siftHome
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sift");

    public static string ReviewsPath => Path.Combine(HomePath, "reviews");

    /// <summary>One file per review with a live Claude watcher.</summary>
    public static string ListenersPath => Path.Combine(HomePath, "listeners");

    /// <summary>Port and pid of the running server, for CLI commands.</summary>
    public static string ServerInfoPath => Path.Combine(HomePath, "server.json");
}
