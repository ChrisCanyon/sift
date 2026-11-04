using System.Diagnostics;
using System.Text;

namespace Sift.Core.Git;

public sealed class GitCommandException(string arguments, int exitCode, string standardError)
    : Exception($"git {arguments} failed with exit code {exitCode}: {standardError.Trim()}")
{
    public int ExitCode { get; } = exitCode;
    public string StandardError { get; } = standardError;
}

public sealed class GitRunner(string repositoryPath)
{
    // Neutralize user config that changes diff output shape.
    private static readonly string[] ConfigOverrides =
    [
        "core.quotePath=false",
        "color.ui=never",
        "diff.noprefix=false",
        "diff.mnemonicPrefix=false",
    ];

    public string RepositoryPath { get; } = repositoryPath;

    public async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        foreach (var configOverride in ConfigOverrides)
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(configOverride);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_PAGER"] = "cat";

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start git.");
        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;

        if (process.ExitCode != 0)
        {
            throw new GitCommandException(string.Join(' ', arguments), process.ExitCode, standardError);
        }

        return standardOutput;
    }
}
