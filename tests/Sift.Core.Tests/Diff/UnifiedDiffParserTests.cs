using Sift.Core.Diff;

namespace Sift.Core.Tests.Diff;

public class UnifiedDiffParserTests
{
    private static string Diff(params string[] lines) => string.Join('\n', lines) + "\n";

    [Fact]
    public void Parse_ModifiedFileWithTwoHunks_TracksLineNumbersPerSide()
    {
        var diff = Diff(
            "diff --git a/src/Calc.cs b/src/Calc.cs",
            "index 1111111..2222222 100644",
            "--- a/src/Calc.cs",
            "+++ b/src/Calc.cs",
            "@@ -1,3 +1,3 @@ namespace Calc",
            " line one",
            "-old two",
            "+new two",
            " line three",
            "@@ -10,2 +10,3 @@ public int Add()",
            " ten",
            "+inserted",
            " eleven");

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        Assert.Equal("src/Calc.cs", file.Path);
        Assert.Equal(FileChangeKind.Modified, file.ChangeKind);
        Assert.Equal(2, file.Hunks.Count);
        Assert.Equal(2, file.AddedLineCount);
        Assert.Equal(1, file.RemovedLineCount);

        var firstHunk = file.Hunks[0];
        Assert.Equal("namespace Calc", firstHunk.SectionHeader);
        Assert.Equal(new DiffLine(DiffLineKind.Removed, "old two", 2, null), firstHunk.Lines[1]);
        Assert.Equal(new DiffLine(DiffLineKind.Added, "new two", null, 2), firstHunk.Lines[2]);
        Assert.Equal(new DiffLine(DiffLineKind.Context, "line three", 3, 3), firstHunk.Lines[3]);

        var secondHunk = file.Hunks[1];
        Assert.Equal(new DiffLine(DiffLineKind.Added, "inserted", null, 11), secondHunk.Lines[1]);
        Assert.Equal(new DiffLine(DiffLineKind.Context, "eleven", 11, 12), secondHunk.Lines[2]);
    }

    [Fact]
    public void Parse_AddedAndDeletedFiles_UseTheSurvivingPathAndOmittedCountsDefaultToOne()
    {
        var diff = Diff(
            "diff --git a/new.txt b/new.txt",
            "new file mode 100644",
            "index 0000000..1111111",
            "--- /dev/null",
            "+++ b/new.txt",
            "@@ -0,0 +1 @@",
            "+hello",
            "diff --git a/gone.txt b/gone.txt",
            "deleted file mode 100755",
            "index 1111111..0000000",
            "--- a/gone.txt",
            "+++ /dev/null",
            "@@ -1 +0,0 @@",
            "-bye");

        var files = UnifiedDiffParser.Parse(diff);

        Assert.Equal(2, files.Count);
        Assert.Equal(("new.txt", FileChangeKind.Added, "100644"), (files[0].Path, files[0].ChangeKind, files[0].NewMode));
        Assert.Equal(("gone.txt", FileChangeKind.Deleted, "100755"), (files[1].Path, files[1].ChangeKind, files[1].OldMode));
        Assert.Equal(1, files[0].Hunks[0].NewLineCount);
        Assert.Equal(1, files[1].Hunks[0].OldLineCount);
    }

    [Fact]
    public void Parse_PureRenameAndBinaryAndModeChange_ProduceFilesWithoutHunks()
    {
        var diff = Diff(
            "diff --git a/Old Name.cs b/New Name.cs",
            "similarity index 100%",
            "rename from Old Name.cs",
            "rename to New Name.cs",
            "diff --git a/logo.png b/logo.png",
            "index 1111111..2222222 100644",
            "Binary files a/logo.png and b/logo.png differ",
            "diff --git a/run.sh b/run.sh",
            "old mode 100644",
            "new mode 100755");

        var files = UnifiedDiffParser.Parse(diff);

        Assert.Equal(3, files.Count);
        Assert.All(files, file => Assert.Empty(file.Hunks));

        Assert.Equal(("New Name.cs", "Old Name.cs", FileChangeKind.Renamed, 100), (files[0].Path, files[0].OldPath, files[0].ChangeKind, files[0].SimilarityPercent));
        Assert.True(files[1].IsBinary);
        Assert.Equal("logo.png", files[1].Path);
        Assert.Equal(("run.sh", "100644", "100755"), (files[2].Path, files[2].OldMode, files[2].NewMode));
    }

    [Fact]
    public void Parse_QuotedPathsWithEscapes_AreDecoded()
    {
        var diff = Diff(
            "diff --git \"a/tab\\there.txt\" \"b/caf\\303\\251.txt\"",
            "similarity index 90%",
            "rename from \"tab\\there.txt\"",
            "rename to \"caf\\303\\251.txt\"",
            "--- \"a/tab\\there.txt\"",
            "+++ \"b/caf\\303\\251.txt\"",
            "@@ -1 +1 @@",
            "-a",
            "+b");

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        Assert.Equal("café.txt", file.Path);
        Assert.Equal("tab\there.txt", file.OldPath);
    }

    [Fact]
    public void Parse_RemovedLineThatLooksLikeFileHeader_StaysInsideTheHunk()
    {
        var diff = Diff(
            "diff --git a/notes.md b/notes.md",
            "--- a/notes.md",
            "+++ b/notes.md",
            "@@ -1,2 +1,2 @@",
            "--- a/fake",
            "+++ b/fake",
            " tail");

        var file = Assert.Single(UnifiedDiffParser.Parse(diff));

        Assert.Equal("notes.md", file.Path);
        Assert.Equal(
            [DiffLineKind.Removed, DiffLineKind.Added, DiffLineKind.Context],
            file.Hunks[0].Lines.Select(line => line.Kind));
        Assert.Equal("-- a/fake", file.Hunks[0].Lines[0].Text);
    }

    [Fact]
    public void Parse_NoNewlineMarkerAndCarriageReturns_ArePreservedOnTheRightLines()
    {
        var diff = Diff(
            "diff --git a/a.txt b/a.txt",
            "--- a/a.txt",
            "+++ b/a.txt",
            "@@ -1 +1 @@",
            "-last\r",
            "\\ No newline at end of file",
            "+last",
            "\\ No newline at end of file");

        var lines = Assert.Single(UnifiedDiffParser.Parse(diff)).Hunks[0].Lines;

        Assert.Equal(new DiffLine(DiffLineKind.Removed, "last\r", 1, null, NoNewlineAtEnd: true), lines[0]);
        Assert.Equal(new DiffLine(DiffLineKind.Added, "last", null, 1, NoNewlineAtEnd: true), lines[1]);
    }

    [Fact]
    public void Parse_HunkIds_IgnoreLineShiftsButDistinguishRepeatedIdenticalChanges()
    {
        static string FileDiff(int firstStart, int secondStart) => Diff(
            "diff --git a/a.cs b/a.cs",
            "--- a/a.cs",
            "+++ b/a.cs",
            $"@@ -{firstStart},2 +{firstStart},2 @@",
            " context A",
            "-Foo();",
            "+Bar();",
            $"@@ -{secondStart},2 +{secondStart},2 @@",
            " context B",
            "-Foo();",
            "+Bar();");

        var original = UnifiedDiffParser.Parse(FileDiff(1, 20)).Single().Hunks;
        var shifted = UnifiedDiffParser.Parse(FileDiff(5, 40)).Single().Hunks;

        Assert.NotEqual(original[0].Id, original[1].Id);
        Assert.Equal(original.Select(hunk => hunk.Id), shifted.Select(hunk => hunk.Id));
    }
}
