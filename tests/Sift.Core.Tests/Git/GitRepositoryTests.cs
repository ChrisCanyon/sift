using Sift.Core.Diff;
using Sift.Core.Git;

namespace Sift.Core.Tests.Git;

public sealed class GitRepositoryTests : IAsyncLifetime
{
    private readonly string repositoryPath = Path.Combine(Path.GetTempPath(), "sift-tests", Guid.NewGuid().ToString("N"));
    private GitRunner gitRunner = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(repositoryPath);
        gitRunner = new GitRunner(repositoryPath);
        await gitRunner.RunAsync(["init", "-q", "-b", "main"]);
        await gitRunner.RunAsync(["config", "user.name", "Sift Tests"]);
        await gitRunner.RunAsync(["config", "user.email", "sift@example.invalid"]);
        await gitRunner.RunAsync(["config", "core.autocrlf", "false"]);
    }

    public Task DisposeAsync()
    {
        foreach (var file in Directory.EnumerateFiles(repositoryPath, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(repositoryPath, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetDiffAsync_MergeBase_ExcludesChangesMadeOnBaseAfterBranching()
    {
        await WriteFileAsync("shared.txt", "one\ntwo\n");
        await CommitAllAsync("initial");
        await gitRunner.RunAsync(["switch", "-q", "-c", "feature"]);
        await WriteFileAsync("feature.txt", "feature work\n");
        await CommitAllAsync("feature");
        await gitRunner.RunAsync(["switch", "-q", "main"]);
        await WriteFileAsync("main-only.txt", "landed on main later\n");
        await CommitAllAsync("main moves on");

        var result = await new GitRepository(gitRunner).GetDiffAsync("main", "feature");

        var file = Assert.Single(result.Files);
        Assert.Equal("feature.txt", file.Path);
        Assert.Equal(FileChangeKind.Added, file.ChangeKind);
        Assert.NotEqual(result.BaseSha, result.FromSha);
    }

    [Fact]
    public async Task GetDiffAsync_Direct_ShowsBaseOnlyHotfixAsRemovedFromHead()
    {
        await WriteFileAsync("billing.cs", "fee = 1;\n");
        await CommitAllAsync("release 1.0");
        await gitRunner.RunAsync(["switch", "-q", "-c", "staging"]);
        await WriteFileAsync("feature.cs", "new feature\n");
        await CommitAllAsync("feature on staging");
        await gitRunner.RunAsync(["switch", "-q", "main"]);
        await WriteFileAsync("billing.cs", "fee = 2;\n");
        await CommitAllAsync("hotfix on release only");

        var repository = new GitRepository(gitRunner);
        var mergeBaseDiff = await repository.GetDiffAsync("main", "staging", DiffBase.MergeBase);
        var directDiff = await repository.GetDiffAsync("main", "staging", DiffBase.Direct);

        Assert.Equal(["feature.cs"], mergeBaseDiff.Files.Select(file => file.Path));
        Assert.Equal(["billing.cs", "feature.cs"], directDiff.Files.Select(file => file.Path).Order());
        Assert.Equal(directDiff.BaseSha, directDiff.FromSha);
        var billingLines = directDiff.Files.Single(file => file.Path == "billing.cs").Hunks.Single().Lines;
        Assert.Contains(billingLines, line => line is { Kind: DiffLineKind.Removed, Text: "fee = 2;" });
        Assert.Contains(billingLines, line => line is { Kind: DiffLineKind.Added, Text: "fee = 1;" });
    }

    [Fact]
    public async Task GetDiffAsync_RealGitOutput_ParsesRenamesAndUnusualPaths()
    {
        var body = string.Join('\n', Enumerable.Range(1, 30).Select(number => $"line {number}")) + "\n";
        await WriteFileAsync("Old Folder/Widget.cs", body);
        await WriteFileAsync("notes.md", "-- dashes\nkeep\n");
        await CommitAllAsync("initial");
        await gitRunner.RunAsync(["switch", "-q", "-c", "feature"]);

        File.Delete(Path.Combine(repositoryPath, "Old Folder", "Widget.cs"));
        await WriteFileAsync("New Folder/Gadget ü.cs", body.Replace("line 15", "line fifteen"));
        await WriteFileAsync("notes.md", "keep\n");
        await CommitAllAsync("rename and edit");

        var result = await new GitRepository(gitRunner).GetDiffAsync("main", "feature");

        var renamed = Assert.Single(result.Files, file => file.ChangeKind == FileChangeKind.Renamed);
        Assert.Equal("New Folder/Gadget ü.cs", renamed.Path);
        Assert.Equal("Old Folder/Widget.cs", renamed.OldPath);
        Assert.Contains(renamed.Hunks.Single().Lines, line => line is { Kind: DiffLineKind.Added, Text: "line fifteen", NewLineNumber: 15 });

        var notes = Assert.Single(result.Files, file => file.Path == "notes.md");
        Assert.Equal("-- dashes", Assert.Single(notes.Hunks.Single().Lines, line => line.Kind == DiffLineKind.Removed).Text);
    }

    [Fact]
    public async Task GetDiffAsync_LineEndingConversion_HidesChurnButKeepsRealEditsAndCountsWhatWasHidden()
    {
        await WriteFileAsync("Members.cs", "class Members\r\n{\r\n    int limit = 10;\r\n    void Run() { }\r\n}\r\n");
        await WriteFileAsync("Only.cs", "line a\r\nline b\r\nline c\r\n");
        await CommitAllAsync("crlf");
        await gitRunner.RunAsync(["switch", "-q", "-c", "feature"]);
        await WriteFileAsync("Members.cs", "class Members\n{\n    int limit = 25;\n    void Run() { }\n}\n");
        await WriteFileAsync("Only.cs", "line a\nline b\nline c\n");
        await CommitAllAsync("convert to lf and change limit");

        var result = await new GitRepository(gitRunner).GetDiffAsync("main", "feature");

        var members = Assert.Single(result.Files, file => file.Path == "Members.cs");
        Assert.Equal((1, 1), (members.AddedLineCount, members.RemovedLineCount));
        Assert.Contains(members.Hunks.Single().Lines, line => line is { Kind: DiffLineKind.Added, Text: "    int limit = 25;" });
        Assert.Equal(4, members.LineEndingChangedLines);

        var lineEndingsOnly = Assert.Single(result.Files, file => file.Path == "Only.cs");
        Assert.Empty(lineEndingsOnly.Hunks);
        Assert.Equal(3, lineEndingsOnly.LineEndingChangedLines);
        Assert.Equal("Line endings only", Sift.Core.Analysis.RuleClassifier.ClassifyFile(lineEndingsOnly)?.Reason);
    }

    [Fact]
    public void ParseNumstat_ReadsPlainRenamedAndBinaryRecords()
    {
        var output = "3\t1\tsrc/a.cs\0" + "2\t2\t\0old/b.cs\0new/b.cs\0" + "-\t-\tlogo.png\0";

        var counts = GitRepository.ParseNumstat(output);

        Assert.Equal((3, 1, null), (counts["src/a.cs"].Added, counts["src/a.cs"].Removed, counts["src/a.cs"].OldPath));
        Assert.Equal((4, "old/b.cs"), (counts["new/b.cs"].Count, counts["new/b.cs"].OldPath));
        Assert.False(counts.ContainsKey("old/b.cs"));
        Assert.Equal((null, null), (counts["logo.png"].Added, counts["logo.png"].Removed));
    }

    [Fact]
    public async Task GetDiffAsync_RealRepository_ReconcilesWithGitIncludingBinaryRenameAndLineEndingOnlyFiles()
    {
        var body = string.Join('\n', Enumerable.Range(1, 30).Select(number => $"line {number}")) + "\n";
        await WriteFileAsync("Old/Widget.cs", body);
        await WriteFileAsync("crlf.txt", "a\r\nb\r\n");
        await File.WriteAllBytesAsync(Path.Combine(repositoryPath, "logo.bin"), [0, 1, 2, 3, 0]);
        await CommitAllAsync("initial");
        await gitRunner.RunAsync(["switch", "-q", "-c", "feature"]);
        File.Delete(Path.Combine(repositoryPath, "Old", "Widget.cs"));
        await WriteFileAsync("New/Widget.cs", body.Replace("line 15", "line fifteen"));
        await WriteFileAsync("crlf.txt", "a\nb\n");
        await File.WriteAllBytesAsync(Path.Combine(repositoryPath, "logo.bin"), [0, 9, 9, 9, 0]);
        await WriteFileAsync("added.cs", "new file\n");
        await CommitAllAsync("mixed changes");

        var result = await new GitRepository(gitRunner).GetDiffAsync("main", "feature");

        var reconciliation = Assert.IsType<Reconciliation>(result.Reconciliation);
        Assert.Empty(reconciliation.Problems);
        Assert.Equal(4, reconciliation.FileCount);
    }

    [Fact]
    public async Task GetFileLinesAsync_ReturnsLinesAtThatCommitWithoutLineTerminators()
    {
        await WriteFileAsync("Billing/Fees.cs", "first\r\nsecond\r\n");
        await CommitAllAsync("crlf version");
        var firstSha = await new GitRepository(gitRunner).ResolveCommitAsync("HEAD");
        await WriteFileAsync("Billing/Fees.cs", "changed\n");
        await CommitAllAsync("lf version");

        var repository = new GitRepository(gitRunner);

        Assert.Equal(["first", "second"], await repository.GetFileLinesAsync(firstSha, "Billing/Fees.cs"));
        Assert.Equal(["changed"], await repository.GetFileLinesAsync("HEAD", "Billing/Fees.cs"));
        await Assert.ThrowsAsync<GitCommandException>(() => repository.GetFileLinesAsync(firstSha, "missing.cs"));
    }

    [Fact]
    public async Task ResolveCommitAsync_UnknownReference_ThrowsGitCommandException()
    {
        await WriteFileAsync("a.txt", "a\n");
        await CommitAllAsync("initial");

        await Assert.ThrowsAsync<GitCommandException>(() => new GitRepository(gitRunner).ResolveCommitAsync("does-not-exist"));
    }

    private async Task WriteFileAsync(string relativePath, string content)
    {
        var fullPath = Path.Combine(repositoryPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content);
    }

    private async Task CommitAllAsync(string message)
    {
        await gitRunner.RunAsync(["add", "-A"]);
        await gitRunner.RunAsync(["commit", "-q", "-m", message]);
    }
}
