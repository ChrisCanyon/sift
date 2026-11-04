using Sift.Core.Diff;

namespace Sift.Core.Tests.Diff;

public class DiffReconcilerTests
{
    private static DiffFile File(string path, int added = 0, int removed = 0, bool binary = false)
    {
        var lines = Enumerable.Range(0, added).Select(index => new DiffLine(DiffLineKind.Added, $"+{index}", null, index + 1))
            .Concat(Enumerable.Range(0, removed).Select(index => new DiffLine(DiffLineKind.Removed, $"-{index}", index + 1, null)))
            .ToList();
        IReadOnlyList<DiffHunk> hunks = lines.Count == 0 ? [] : [new DiffHunk($"{path}-h", 1, removed, 1, added, "", lines)];
        return new DiffFile(path, null, FileChangeKind.Modified, binary, null, null, null, hunks);
    }

    private static Dictionary<string, NumstatEntry> Numstat(params (string Path, int? Added, int? Removed)[] entries) =>
        entries.ToDictionary(entry => entry.Path, entry => new NumstatEntry(entry.Added, entry.Removed, null), StringComparer.Ordinal);

    [Fact]
    public void Reconcile_MatchingDiff_HasNoProblemsAndReportsGitTotals()
    {
        var files = new[] { File("a.cs", added: 3, removed: 1), File("logo.png", binary: true) };
        var numstat = Numstat(("a.cs", 3, 1), ("logo.png", null, null));

        var reconciliation = DiffReconciler.Reconcile(files, numstat, numstat);

        Assert.True(reconciliation.IsComplete);
        Assert.Equal((2, 3, 1), (reconciliation.FileCount, reconciliation.AddedLines, reconciliation.RemovedLines));
    }

    [Fact]
    public void Reconcile_FileGitReportsButDiffLacks_IsMissing()
    {
        var reconciliation = DiffReconciler.Reconcile([File("a.cs", added: 1)], Numstat(("a.cs", 1, 0), ("dropped.cs", 5, 2)), Numstat(("a.cs", 1, 0)));

        var problem = Assert.Single(reconciliation.Problems);
        Assert.Equal((ReconciliationProblemKind.MissingFile, "dropped.cs"), (problem.Kind, problem.Path));
    }

    [Fact]
    public void Reconcile_ParsedLinesDisagreeWithGit_IsALineCountMismatch()
    {
        var reconciliation = DiffReconciler.Reconcile([File("a.cs", added: 2)], Numstat(("a.cs", 3, 0)), Numstat(("a.cs", 3, 0)));

        var problem = Assert.Single(reconciliation.Problems);
        Assert.Equal(ReconciliationProblemKind.LineCountMismatch, problem.Kind);
        Assert.Contains("git +3/-0, parsed +2/-0", problem.Detail);
    }

    [Fact]
    public void Reconcile_FileNotInGitOrListedTwice_IsUnexpected()
    {
        var reconciliation = DiffReconciler.Reconcile(
            [File("a.cs", added: 1), File("a.cs", added: 1), File("ghost.cs", added: 1)],
            Numstat(("a.cs", 1, 0)),
            Numstat(("a.cs", 1, 0)));

        Assert.Equal(
            [("a.cs", ReconciliationProblemKind.UnexpectedFile), ("ghost.cs", ReconciliationProblemKind.UnexpectedFile)],
            reconciliation.Problems.Where(problem => problem.Kind == ReconciliationProblemKind.UnexpectedFile).Select(problem => (problem.Path, problem.Kind)).Order());
    }

    [Fact]
    public void Reconcile_LineEndingOnlyFileWithNoHunks_IsAccountedForByPresence()
    {
        var reconciliation = DiffReconciler.Reconcile([File("crlf.txt")], Numstat(("crlf.txt", 2, 2)), Numstat(("crlf.txt", 0, 0)));

        Assert.True(reconciliation.IsComplete);
    }
}
