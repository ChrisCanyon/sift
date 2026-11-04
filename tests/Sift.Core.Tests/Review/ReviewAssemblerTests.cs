using Sift.Core.Git;
using Sift.Core.Analysis;
using Sift.Core.Diff;
using Sift.Core.Review;

namespace Sift.Core.Tests.Review;

public class ReviewAssemblerTests
{
    private static readonly ReviewSource Source = new("C:/repo", "base", "head");

    private static DiffHunk Hunk(string id) =>
        new(id, 1, 1, 1, 1, "", [new DiffLine(DiffLineKind.Added, id, null, 1)]);

    private static DiffFile File(string path, params string[] hunkIds) =>
        new(path, null, FileChangeKind.Modified, false, null, null, null, hunkIds.Select(Hunk).ToList());

    private static DiffResult Diff(params DiffFile[] files) => new("base", "head", "merge", DiffBase.MergeBase, files);

    private static ReviewView Assemble(DiffResult diff, IReadOnlyList<FileClassification>? files = null, IReadOnlyList<HunkClassification>? hunks = null) =>
        ReviewAssembler.Assemble(new ReviewDocument(1, Source, Files: files, Hunks: hunks), diff);

    [Fact]
    public void Assemble_HunkClassificationOverridesFile_SplittingTheFileAcrossBuckets()
    {
        var view = Assemble(
            Diff(File("src/Service.cs", "h1", "h2", "h3")),
            files: [new FileClassification("src/Service.cs", "refactor", Reason: "Rename Foo to Bar")],
            hunks: [new HunkClassification("h2", "core", Priority.High, "Changes retry limit")]);

        Assert.Equal(["core", "refactor"], view.Buckets.Select(bucket => bucket.Id));

        var coreFile = Assert.Single(view.Buckets[0].Files);
        Assert.Equal(["h2"], coreFile.Hunks.Select(hunk => hunk.Id));
        Assert.Equal(Priority.High, coreFile.Priority);
        Assert.Null(coreFile.Reason);
        Assert.Equal(3, coreFile.TotalHunkCount);

        var refactorFile = Assert.Single(view.Buckets[1].Files);
        Assert.Equal(["h1", "h3"], refactorFile.Hunks.Select(hunk => hunk.Id));
        Assert.Equal("Rename Foo to Bar", refactorFile.Reason);
        Assert.All(refactorFile.Hunks, hunk => Assert.Null(hunk.Reason));
    }

    [Fact]
    public void Assemble_UnclassifiedHunksAndHunklessFiles_LandInUnsortedAtTheTop()
    {
        var emptyChange = new DiffFile("New.cs", null, FileChangeKind.Modified, false, null, null, null, []);

        var view = Assemble(
            Diff(File("a.cs", "h1", "h2"), emptyChange),
            hunks: [new HunkClassification("h1", "tests")]);

        Assert.Equal(ReviewAssembler.UnsortedBucketId, view.Buckets[0].Id);
        Assert.Equal(["a.cs", "New.cs"], view.Buckets[0].Files.Select(file => file.Path));
        Assert.Equal(2, view.Buckets[0].HunkCount);
        Assert.Equal("tests", view.Buckets[1].Id);
    }

    [Fact]
    public void Assemble_RenamedFileClassifiedByOldPath_IsMatched()
    {
        var renamed = new DiffFile("New.cs", "Old.cs", FileChangeKind.Renamed, false, 100, null, null, []);

        var view = Assemble(Diff(renamed), files: [new FileClassification("Old.cs", "refactor")]);

        Assert.Equal("refactor", Assert.Single(view.Buckets).Id);
        Assert.Empty(view.UnmatchedFilePaths);
    }

    [Fact]
    public void Assemble_FilesWithinBucket_OrderByPriorityThenPath()
    {
        var view = Assemble(
            Diff(File("b.cs", "b1"), File("a.cs", "a1"), File("c.cs", "c1")),
            hunks:
            [
                new HunkClassification("a1", "core", Priority.Low),
                new HunkClassification("b1", "core"),
                new HunkClassification("c1", "core", Priority.Critical),
            ]);

        Assert.Equal(["c.cs", "b.cs", "a.cs"], Assert.Single(view.Buckets).Files.Select(file => file.Path));
    }

    [Fact]
    public void Assemble_UnknownBucketId_IsShownAfterKnownBucketsInsteadOfDropped()
    {
        var view = Assemble(
            Diff(File("a.cs", "a1"), File("b.cs", "b1")),
            hunks: [new HunkClassification("a1", "payment-flow"), new HunkClassification("b1", "noise")]);

        Assert.Equal(["noise", "payment-flow"], view.Buckets.Select(bucket => bucket.Id));
        Assert.Equal("payment-flow", view.Buckets[1].Title);
    }

    [Fact]
    public void Assemble_ClassificationsNotInDiff_AreReportedAsUnmatched()
    {
        var view = Assemble(
            Diff(File("a.cs", "a1")),
            files: [new FileClassification("gone.cs", "core")],
            hunks: [new HunkClassification("a1", "core"), new HunkClassification("stale-id", "core")]);

        Assert.Equal(["stale-id"], view.UnmatchedHunkIds);
        Assert.Equal(["gone.cs"], view.UnmatchedFilePaths);
    }

    [Fact]
    public void Assemble_HunkWithoutPriority_InheritsFilePriority()
    {
        var view = Assemble(
            Diff(File("pay.cs", "p1", "p2")),
            files: [new FileClassification("pay.cs", "core", Priority.Critical)],
            hunks: [new HunkClassification("p1", "core", Reason: "Rounding change")]);

        Assert.All(Assert.Single(Assert.Single(view.Buckets).Files).Hunks, hunk => Assert.Equal(Priority.Critical, hunk.Priority));
    }
}

public class ReviewAssemblerPatternTests
{
    private static readonly ReviewSource Source = new("C:/repo", "base", "head");

    private static DiffHunk Hunk(string id, string removed, string added) =>
        new(id, 1, 1, 1, 1, "", [new DiffLine(DiffLineKind.Removed, removed, 1, null), new DiffLine(DiffLineKind.Added, added, null, 1)]);

    private static DiffFile File(string path, params DiffHunk[] hunks) =>
        new(path, null, FileChangeKind.Modified, false, null, null, null, hunks);

    private static DiffResult RenameAcrossThreeFiles(DiffHunk? extraHunkInFirstFile = null) => new("base", "head", "merge", DiffBase.MergeBase,
    [
        File("a.cs", extraHunkInFirstFile is null ? [Hunk("a1", "Old.Run(1);", "New.Run(1);")] : [Hunk("a1", "Old.Run(1);", "New.Run(1);"), extraHunkInFirstFile]),
        File("b.cs", Hunk("b1", "x = Old.Make();", "x = New.Make();")),
        File("c.cs", Hunk("c1", "using Old;", "using New;")),
    ]);

    [Fact]
    public void Assemble_RenamePattern_BecomesOneCollapsedRefactorGroupByRule()
    {
        var view = ReviewAssembler.Assemble(new ReviewDocument(1, Source), RenameAcrossThreeFiles());

        var bucket = Assert.Single(view.Buckets);
        Assert.Equal("refactor", bucket.Id);
        Assert.Empty(bucket.Files);
        var pattern = Assert.Single(bucket.Patterns);
        Assert.Equal((Classifier.Rule, 3, 3), (pattern.ClassifiedBy, pattern.Members.Count, pattern.FileCount));
        Assert.Equal(3, bucket.HunkCount);
    }

    [Fact]
    public void Assemble_AiPatternClassification_BeatsRuleAndFileClassification()
    {
        var patternId = PatternDetector_DetectSingleId(RenameAcrossThreeFiles());
        var document = new ReviewDocument(1, Source,
            Files: [new FileClassification("a.cs", "core", Priority.High)],
            Patterns: [new PatternClassification(patternId, "contract", Priority.High, "Public type renamed")]);

        var view = ReviewAssembler.Assemble(document, RenameAcrossThreeFiles());

        var pattern = Assert.Single(Assert.Single(view.Buckets).Patterns);
        Assert.Equal(("Public type renamed", Classifier.Ai), (pattern.Reason, pattern.ClassifiedBy));
        Assert.Equal("contract", view.Buckets[0].Id);
    }

    [Fact]
    public void Assemble_AiHunkClassification_PullsAMemberOutOfItsPattern()
    {
        var view = ReviewAssembler.Assemble(
            new ReviewDocument(1, Source, Hunks: [new HunkClassification("b1", "core", Priority.Critical, "Factory now caches")]),
            RenameAcrossThreeFiles());

        Assert.Equal(["core", "refactor"], view.Buckets.Select(bucket => bucket.Id));
        Assert.Equal("b1", Assert.Single(Assert.Single(view.Buckets[0].Files).Hunks).Id);
        Assert.Equal(["a1", "c1"], Assert.Single(view.Buckets[1].Patterns).Members.Select(member => member.Hunk.Id));
        Assert.Empty(view.UnmatchedHunkIds);
    }

    [Fact]
    public void Assemble_WhitespaceHunkInAiClassifiedFile_StillGoesToNoise()
    {
        var whitespace = Hunk("a2", "if (x)  {", "if (x) {");
        var view = ReviewAssembler.Assemble(
            new ReviewDocument(1, Source, Files: [new FileClassification("a.cs", "core")]),
            RenameAcrossThreeFiles(whitespace));

        var noise = Assert.Single(view.Buckets, bucket => bucket.Id == "noise");
        Assert.Equal("a2", Assert.Single(Assert.Single(noise.Files).Hunks).Id);
        Assert.Equal("Whitespace only", noise.Files[0].Hunks[0].Reason);
    }

    [Fact]
    public void Assemble_UnknownPatternId_IsReportedAsUnmatched()
    {
        var view = ReviewAssembler.Assemble(
            new ReviewDocument(1, Source, Patterns: [new PatternClassification("pdeadbeef", "core")]),
            RenameAcrossThreeFiles());

        Assert.Equal(["pdeadbeef"], view.UnmatchedPatternIds);
    }

    private static string PatternDetector_DetectSingleId(DiffResult diff) =>
        Assert.Single(Sift.Core.Analysis.PatternDetector.Detect(diff)).Id;
}

public class ReviewAssemblerPlacementTests
{
    [Fact]
    public void Assemble_MixedReview_PlacesEveryHunkAndHunklessFileExactlyOnce()
    {
        static DiffHunk Hunk(string id, string removed, string added) =>
            new(id, 1, 1, 1, 1, "", [new DiffLine(DiffLineKind.Removed, removed, 1, null), new DiffLine(DiffLineKind.Added, added, null, 1)]);

        var diff = new DiffResult("base", "head", "from", Sift.Core.Git.DiffBase.MergeBase,
        [
            new DiffFile("a.cs", null, FileChangeKind.Modified, false, null, null, null, [Hunk("a1", "Old.Run();", "New.Run();"), Hunk("a2", "x  = 1;", "x = 1;"), Hunk("a3", "fee = 1;", "fee = 2;")]),
            new DiffFile("b.cs", null, FileChangeKind.Modified, false, null, null, null, [Hunk("b1", "Old.Make();", "New.Make();")]),
            new DiffFile("c.cs", null, FileChangeKind.Modified, false, null, null, null, [Hunk("c1", "using Old;", "using New;")]),
            new DiffFile("package-lock.json", null, FileChangeKind.Modified, false, null, null, null, [Hunk("l1", "\"v\": 1", "\"v\": 2")]),
            new DiffFile("Moved.cs", "Old/Moved.cs", FileChangeKind.Renamed, false, 100, null, null, []),
            new DiffFile("crlf.txt", null, FileChangeKind.Modified, false, null, null, null, []) { LineEndingChangedLines = 4 },
        ]);
        var document = new ReviewDocument(1, new ReviewSource("C:/repo", "base", "head"),
            Files: [new FileClassification("a.cs", "core")],
            Hunks: [new HunkClassification("b1", "contract", Priority.High)]);

        var view = ReviewAssembler.Assemble(document, diff);

        Assert.True(view.Reconciliation.IsComplete, string.Join("; ", view.Reconciliation.Problems.Select(problem => problem.Detail)));
        var placedHunks = view.Buckets.SelectMany(bucket => bucket.Files.SelectMany(file => file.Hunks).Concat(bucket.Patterns.SelectMany(pattern => pattern.Members.Select(member => member.Hunk)))).Select(hunk => hunk.Id);
        Assert.Equal(["a1", "a2", "a3", "b1", "c1", "l1"], placedHunks.Order());
    }
}
