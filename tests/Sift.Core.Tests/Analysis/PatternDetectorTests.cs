using Sift.Core.Git;
using Sift.Core.Analysis;
using Sift.Core.Diff;

namespace Sift.Core.Tests.Analysis;

public class PatternDetectorTests
{
    private static int nextHunkNumber;

    private static DiffHunk Hunk(params string[] changedLines)
    {
        var lines = new List<DiffLine> { new(DiffLineKind.Context, "context", 1, 1) };
        foreach (var changedLine in changedLines)
        {
            var kind = changedLine[0] == '+' ? DiffLineKind.Added : DiffLineKind.Removed;
            lines.Add(new DiffLine(kind, changedLine[1..], null, null));
        }

        return new DiffHunk($"h{Interlocked.Increment(ref nextHunkNumber)}", 1, 1, 1, 1, "", lines);
    }

    private static DiffFile File(string path, params DiffHunk[] hunks) =>
        new(path, null, FileChangeKind.Modified, false, null, null, null, hunks);

    private static DiffResult Diff(params DiffFile[] files) => new("base", "head", "merge", DiffBase.MergeBase, files);

    [Fact]
    public void Detect_SameRenameAtDifferentCallSites_GroupsIntoOneIdentifierReplacement()
    {
        var first = Hunk("-    var total = OrderCalculator.Sum(items);", "+    var total = PriceCalculator.Sum(items);");
        var second = Hunk("-        return OrderCalculator.Max(a, b);", "+        return PriceCalculator.Max(a, b);");
        var third = Hunk("-using static OrderCalculator;", "+using static PriceCalculator;");

        var pattern = Assert.Single(PatternDetector.Detect(Diff(File("a.cs", first, second), File("b.cs", third))));

        Assert.Equal(PatternKind.Replacement, pattern.Kind);
        Assert.True(pattern.IsIdentifierReplacement);
        Assert.Equal("OrderCalculator → PriceCalculator", pattern.Description);
        Assert.Equal([first.Id, second.Id, third.Id], pattern.Members.Select(member => member.HunkId));
        Assert.Equal(2, pattern.FileCount);
    }

    [Fact]
    public void Detect_HunkWithRenamePlusAnotherEdit_StaysOutOfTheGroup()
    {
        var renames = Enumerable.Range(0, 3)
            .Select(index => Hunk($"-Old.Call({index});", $"+New.Call({index});"))
            .ToArray();
        var deviant = Hunk("-Old.Call(retries > 3);", "+New.Call(retries >= 3);");

        var pattern = Assert.Single(PatternDetector.Detect(Diff(File("a.cs", [.. renames, deviant]))));

        Assert.DoesNotContain(pattern.Members, member => member.HunkId == deviant.Id);
        Assert.Equal(3, pattern.Members.Count);
    }

    [Fact]
    public void Detect_RepeatedOperatorChange_IsGroupedButNotTreatedAsARename()
    {
        var hunks = Enumerable.Range(0, 3)
            .Select(index => Hunk($"-if (count{index} > 0)", $"+if (count{index} >= 0)"))
            .ToArray();

        var pattern = Assert.Single(PatternDetector.Detect(Diff(File("a.cs", hunks))));

        Assert.False(pattern.IsIdentifierReplacement);
        Assert.Null(RuleClassifier.ClassifyPattern(pattern));
        Assert.Equal("> → >=", pattern.Description);
    }

    [Fact]
    public void Detect_QualifiedNamespaceMove_KeepsOriginalSpacingAndCountsAsRename()
    {
        var hunks = new[] { "a", "b", "c" }
            .Select(name => Hunk($"-using Company.Legacy.{name};", $"+using Company.Core.{name};"))
            .ToArray();

        var pattern = Assert.Single(PatternDetector.Detect(Diff(File("a.cs", hunks))));

        Assert.Equal("Legacy → Core", pattern.Description);
        Assert.True(pattern.IsIdentifierReplacement);
    }

    [Fact]
    public void Detect_SameLinesAddedEverywhere_GroupsAsRepeatedLines()
    {
        var files = Enumerable.Range(0, 4)
            .Select(index => File($"f{index}.cs", Hunk("+using System.Linq;")))
            .ToArray();

        var pattern = Assert.Single(PatternDetector.Detect(Diff(files)));

        Assert.Equal(PatternKind.RepeatedLines, pattern.Kind);
        Assert.Equal("+using System.Linq;", pattern.Description);
        Assert.Equal(4, pattern.FileCount);
    }

    [Fact]
    public void Detect_FewerThanMinimumOccurrencesOrWhitespaceOnly_ProducesNoGroups()
    {
        var twice = new[] { Hunk("-A();", "+B();"), Hunk("-A();", "+B();") };
        var whitespace = Enumerable.Range(0, 3).Select(_ => Hunk("-x  = 1;", "+x = 1;")).ToArray();

        Assert.Empty(PatternDetector.Detect(Diff(File("a.cs", [.. twice, .. whitespace]))));
    }

    [Fact]
    public void IsWhitespaceOnly_IgnoresIndentationAndLineSplitsButNotContent()
    {
        Assert.True(PatternDetector.IsWhitespaceOnly(Hunk("-foo(a, b);", "+foo(a,", "+    b);")));
        Assert.True(PatternDetector.IsWhitespaceOnly(Hunk("-x\r", "+x")));
        Assert.False(PatternDetector.IsWhitespaceOnly(Hunk("-foo(a, b);", "+foo(b, a);")));
    }
}

public class HunkIndexPatternRuleTests
{
    private static DiffHunk Hunk(string id, string removed, string added) =>
        new(id, 1, 1, 1, 1, "", [new DiffLine(DiffLineKind.Removed, removed, 1, null), new DiffLine(DiffLineKind.Added, added, null, 1)]);

    private static DiffFile File(string path, params DiffHunk[] hunks) => new(path, null, FileChangeKind.Modified, false, null, null, null, hunks);

    [Fact]
    public void FromDiff_PatternOnlyInsideLockfiles_InheritsTheLockfileRule()
    {
        var diff = new DiffResult("base", "head", "from", DiffBase.MergeBase,
            Enumerable.Range(0, 3).Select(index => File($"app{index}/package-lock.json", Hunk($"h{index}", "\"version\": \"33\"", "\"version\": \"34\""))).ToList());

        var pattern = Assert.Single(HunkIndex.FromDiff(diff, 2, compact: true).Patterns);

        Assert.Equal(("noise", "Only in lockfiles"), (pattern.Rule?.Bucket, pattern.Rule?.Reason));
    }

    [Fact]
    public void FromDiff_PatternAlsoInRealCode_StillNeedsJudgment()
    {
        var diff = new DiffResult("base", "head", "from", DiffBase.MergeBase,
        [
            File("app0/package-lock.json", Hunk("h0", "\"version\": \"33\"", "\"version\": \"34\"")),
            File("app1/package-lock.json", Hunk("h1", "\"version\": \"33\"", "\"version\": \"34\"")),
            File("app2/package.json", Hunk("h2", "\"version\": \"33\"", "\"version\": \"34\"")),
        ]);

        Assert.Null(Assert.Single(HunkIndex.FromDiff(diff, 2, compact: true).Patterns).Rule);
    }
}
