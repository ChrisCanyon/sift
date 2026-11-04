using Sift.Core.Diff;

namespace Sift.Core.Git;

public enum DiffBase
{
    /// <summary>Head against its merge base with base, like a GitHub PR or compare view.</summary>
    MergeBase,

    /// <summary>Base tree against head tree: exactly what differs between two builds.</summary>
    Direct,
}

public sealed class GitRepository(GitRunner gitRunner)
{
    private const string IgnoreLineEndings = "--ignore-cr-at-eol";

    public GitRepository(string repositoryPath) : this(new GitRunner(repositoryPath))
    {
    }

    public async Task<string> ResolveCommitAsync(string reference, CancellationToken cancellationToken = default)
    {
        var output = await gitRunner.RunAsync(["rev-parse", "--verify", "--end-of-options", $"{reference}^{{commit}}"], cancellationToken);
        return output.Trim();
    }

    public async Task<string> GetMergeBaseAsync(string baseSha, string headSha, CancellationToken cancellationToken = default)
    {
        var output = await gitRunner.RunAsync(["merge-base", baseSha, headSha], cancellationToken);
        return output.Trim();
    }

    /// <summary>File content at a commit, split into lines without terminators.</summary>
    public async Task<IReadOnlyList<string>> GetFileLinesAsync(string commitSha, string path, CancellationToken cancellationToken = default)
    {
        var content = await gitRunner.RunAsync(["show", $"{commitSha}:{path}"], cancellationToken);
        var lines = content.Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0)
        {
            lines = lines[..^1];
        }

        return lines.Select(line => line.TrimEnd('\r')).ToList();
    }

    /// <summary>
    /// Line-ending-only changes (CRLF/LF churn) are left out of the hunks; each file reports how many
    /// changed lines that hid, so the conversion is still visible.
    /// </summary>
    public async Task<DiffResult> GetDiffAsync(string baseReference, string headReference, DiffBase diffBase = DiffBase.MergeBase, CancellationToken cancellationToken = default)
    {
        var baseSha = await ResolveCommitAsync(baseReference, cancellationToken);
        var headSha = await ResolveCommitAsync(headReference, cancellationToken);
        var fromSha = diffBase == DiffBase.Direct ? baseSha : await GetMergeBaseAsync(baseSha, headSha, cancellationToken);

        var diffOutput = await gitRunner.RunAsync(
        [
            "diff",
            "--no-color",
            "--no-ext-diff",
            "--no-textconv",
            "--find-renames",
            IgnoreLineEndings,
            "--unified=3",
            "--src-prefix=a/",
            "--dst-prefix=b/",
            fromSha,
            headSha,
        ], cancellationToken);

        var allChangedLines = await GetChangedLineCountsAsync(fromSha, headSha, ignoreLineEndings: false, cancellationToken);
        var contentChangedLines = await GetChangedLineCountsAsync(fromSha, headSha, ignoreLineEndings: true, cancellationToken);

        // Each line-ending-only line counts once as removed and once as added.
        int HiddenLineCount(string path) =>
            ((allChangedLines.GetValueOrDefault(path)?.Count ?? 0) - (contentChangedLines.GetValueOrDefault(path)?.Count ?? 0)) / 2;

        var files = UnifiedDiffParser.Parse(diffOutput)
            .Select(file => HiddenLineCount(file.Path) is var hidden and > 0 ? file with { LineEndingChangedLines = hidden } : file)
            .ToList();

        // Git drops files whose only change is line endings from the patch, so add them back without hunks.
        var parsedPaths = files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var (path, numstat) in allChangedLines)
        {
            if (parsedPaths.Contains(path) || HiddenLineCount(path) <= 0)
            {
                continue;
            }

            var changeKind = numstat.OldPath is null ? FileChangeKind.Modified : FileChangeKind.Renamed;
            files.Add(new DiffFile(path, numstat.OldPath, changeKind, false, null, null, null, []) { LineEndingChangedLines = HiddenLineCount(path) });
        }

        var reconciliation = DiffReconciler.Reconcile(files, allChangedLines, contentChangedLines);
        return new DiffResult(baseSha, headSha, fromSha, diffBase, files) { Reconciliation = reconciliation };
    }

    /// <summary>Line counts per new path, from NUL-separated numstat output.</summary>
    private async Task<Dictionary<string, NumstatEntry>> GetChangedLineCountsAsync(string fromSha, string headSha, bool ignoreLineEndings, CancellationToken cancellationToken)
    {
        List<string> arguments = ["diff", "--numstat", "-z", "--no-ext-diff", "--no-textconv", "--find-renames"];
        if (ignoreLineEndings)
        {
            arguments.Add(IgnoreLineEndings);
        }

        arguments.AddRange([fromSha, headSha]);
        var output = await gitRunner.RunAsync(arguments, cancellationToken);
        return ParseNumstat(output);
    }

    // "<added>\t<removed>\t<path>\0", or for renames "<added>\t<removed>\t\0<old path>\0<new path>\0". Binary files report "-".
    internal static Dictionary<string, NumstatEntry> ParseNumstat(string output)
    {
        var countsByPath = new Dictionary<string, NumstatEntry>(StringComparer.Ordinal);
        var fields = output.Split('\0');
        var index = 0;
        while (index < fields.Length)
        {
            var record = fields[index++];
            var parts = record.Split('\t', 3);
            if (parts.Length < 3)
            {
                continue;
            }

            var path = parts[2];
            string? oldPath = null;
            if (path.Length == 0 && index + 1 < fields.Length)
            {
                oldPath = fields[index++];
                path = fields[index++];
            }

            countsByPath[path] = new NumstatEntry(
                int.TryParse(parts[0], out var added) ? added : null,
                int.TryParse(parts[1], out var removed) ? removed : null,
                oldPath);
        }

        return countsByPath;
    }
}
