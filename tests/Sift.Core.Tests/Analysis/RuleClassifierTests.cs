using Sift.Core.Analysis;
using Sift.Core.Diff;

namespace Sift.Core.Tests.Analysis;

public class RuleClassifierTests
{
    private static readonly DiffHunk ContentHunk =
        new("h", 1, 1, 1, 1, "", [new DiffLine(DiffLineKind.Added, "code", null, 1)]);

    private static DiffFile File(string path, FileChangeKind change = FileChangeKind.Modified, bool binary = false, bool withHunk = true, string? oldMode = null, string? newMode = null) =>
        new(path, change == FileChangeKind.Renamed ? "old/" + path : null, change, binary, null, oldMode, newMode, withHunk ? [ContentHunk] : []);

    [Theory]
    [InlineData("src/ui/package-lock.json", "noise")]
    [InlineData("Api/Migrations/AppDbContextModelSnapshot.cs", "noise")]
    [InlineData("Forms/Main.Designer.cs", "noise")]
    [InlineData("wwwroot/lib/jquery/jquery.js", "noise")]
    [InlineData("site/app.min.js", "noise")]
    [InlineData("docs/setup/index.html", "docs")]
    [InlineData("README.md", "docs")]
    [InlineData("tests/Sift.Core.Tests/ParserTests.cs", "tests")]
    [InlineData("src/Billing.UnitTests/InvoiceFixture.cs", "tests")]
    [InlineData("src/app/cart.spec.ts", "tests")]
    public void ClassifyFile_KnownPaths_MapToTheirBucket(string path, string expectedBucket)
    {
        Assert.Equal(expectedBucket, RuleClassifier.ClassifyFile(File(path))?.Bucket);
    }

    [Theory]
    [InlineData("src/Billing/InvoiceService.cs")]
    [InlineData("src/Testing/Contest.cs")]
    [InlineData("src/distribution/Router.cs")]
    public void ClassifyFile_OrdinaryCode_IsLeftForTheAi(string path)
    {
        Assert.Null(RuleClassifier.ClassifyFile(File(path)));
    }

    [Fact]
    public void ClassifyFile_HunklessChanges_AreClassifiedByKind()
    {
        Assert.Equal("refactor", RuleClassifier.ClassifyFile(File("a.cs", FileChangeKind.Renamed, withHunk: false))?.Bucket);
        Assert.Equal("noise", RuleClassifier.ClassifyFile(File("run.sh", withHunk: false, oldMode: "100644", newMode: "100755"))?.Bucket);
        Assert.Equal("noise", RuleClassifier.ClassifyFile(File("logo.png", binary: true, withHunk: false))?.Reason == "Binary file" ? "noise" : null);
    }
}
