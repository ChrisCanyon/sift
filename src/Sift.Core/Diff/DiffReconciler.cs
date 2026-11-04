namespace Sift.Core.Diff;

public enum ReconciliationProblemKind
{
    /// <summary>Git reports the file as changed but the diff has no entry for it.</summary>
    MissingFile,

    /// <summary>The diff has a file git does not report as changed.</summary>
    UnexpectedFile,

    /// <summary>The parsed hunks don't add up to git's own line counts for the file.</summary>
    LineCountMismatch,

    /// <summary>A hunk or file wasn't placed in any bucket, or was placed more than once.</summary>
    NotPlaced,
}

public sealed record ReconciliationProblem(ReconciliationProblemKind Kind, string Path, string Detail);

/// <summary>Git's own totals for the range, and anything Sift would render differently from them.</summary>
public sealed record Reconciliation(int FileCount, int AddedLines, int RemovedLines, IReadOnlyList<ReconciliationProblem> Problems)
{
    public bool IsComplete => Problems.Count == 0;

    public Reconciliation WithProblems(IEnumerable<ReconciliationProblem> additionalProblems) =>
        this with { Problems = [.. Problems, .. additionalProblems] };
}

/// <summary>Per-file counts from git diff --numstat; null line counts mean git treated the file as binary.</summary>
public sealed record NumstatEntry(int? Added, int? Removed, string? OldPath)
{
    public int Count => (Added ?? 0) + (Removed ?? 0);
}

/// <summary>
/// Checks the parsed diff against git's own numbers, independently of the patch parser: the file set must match
/// plain numstat (no flags), and each file's parsed lines must match numstat taken with the same flags as the patch.
/// </summary>
public static class DiffReconciler
{
    public static Reconciliation Reconcile(
        IReadOnlyList<DiffFile> files,
        IReadOnlyDictionary<string, NumstatEntry> plainNumstat,
        IReadOnlyDictionary<string, NumstatEntry> patchFlagsNumstat)
    {
        var problems = new List<ReconciliationProblem>();
        var filesByPath = files.GroupBy(file => file.Path, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        foreach (var (path, group) in filesByPath.Where(entry => entry.Value.Count > 1))
        {
            problems.Add(new ReconciliationProblem(ReconciliationProblemKind.UnexpectedFile, path, $"Appears {group.Count} times in the diff"));
        }

        foreach (var path in plainNumstat.Keys.Where(path => !filesByPath.ContainsKey(path)).Order(StringComparer.Ordinal))
        {
            problems.Add(new ReconciliationProblem(ReconciliationProblemKind.MissingFile, path, "Changed according to git but not in the diff"));
        }

        foreach (var file in files.Where(file => !plainNumstat.ContainsKey(file.Path)))
        {
            problems.Add(new ReconciliationProblem(ReconciliationProblemKind.UnexpectedFile, file.Path, "In the diff but not changed according to git"));
        }

        foreach (var file in files)
        {
            if (!patchFlagsNumstat.TryGetValue(file.Path, out var expected) || expected.Added is null || expected.Removed is null)
            {
                // Binary, or only line endings changed: presence is all git can vouch for.
                continue;
            }

            if (file.AddedLineCount != expected.Added || file.RemovedLineCount != expected.Removed)
            {
                problems.Add(new ReconciliationProblem(
                    ReconciliationProblemKind.LineCountMismatch,
                    file.Path,
                    $"git +{expected.Added}/-{expected.Removed}, parsed +{file.AddedLineCount}/-{file.RemovedLineCount}"));
            }
        }

        return new Reconciliation(
            plainNumstat.Count,
            plainNumstat.Values.Sum(entry => entry.Added ?? 0),
            plainNumstat.Values.Sum(entry => entry.Removed ?? 0),
            problems);
    }
}
