using Sift.Core.Analysis;
using Sift.Core.Diff;
using Sift.Core.Git;

namespace Sift.Core.Tests.Analysis;

public class MoveDetectorTests
{
    private static readonly string[] Method =
    [
        "    public decimal ApplyFee(decimal amount)",
        "    {",
        "        var fee = amount * FeeRate;",
        "        return amount + fee;",
        "    }",
    ];

    private static DiffHunk Removed(string id, int oldStart, params string[] lines) =>
        new(id, oldStart, lines.Length, oldStart, 0, "",
            lines.Select((text, index) => new DiffLine(DiffLineKind.Removed, text, oldStart + index, null)).ToList());

    private static DiffHunk Added(string id, int newStart, params string[] lines) =>
        new(id, newStart, 0, newStart, lines.Length, "",
            lines.Select((text, index) => new DiffLine(DiffLineKind.Added, text, null, newStart + index)).ToList());

    private static DiffFile File(string path, params DiffHunk[] hunks) =>
        new(path, null, FileChangeKind.Modified, false, null, null, null, hunks);

    private static DiffResult Diff(params DiffFile[] files) => new("base", "head", "from", DiffBase.MergeBase, files);

    [Fact]
    public void Detect_MethodMovedToAnotherFile_MarksBothSidesWithEachOthersLocation()
    {
        var source = Removed("r1", 40, Method);
        var target = Added("a1", 12, [.. Method.Select(line => line.TrimStart())]);

        var moves = MoveDetector.Detect(Diff(File("Billing/Invoice.cs", source), File("Billing/FeeCalculator.cs", target)));

        var from = Assert.Single(moves.SpansFor("a1"));
        Assert.Equal((MoveDirection.From, "Billing/Invoice.cs", 40, 5), (from.Direction, from.OtherPath, from.OtherLine, from.LineCount));
        var to = Assert.Single(moves.SpansFor("r1"));
        Assert.Equal((MoveDirection.To, "Billing/FeeCalculator.cs", 12), (to.Direction, to.OtherPath, to.OtherLine));
        Assert.True(moves.IsFullyMoved(source));
        Assert.Equal("refactor", RuleClassifier.ClassifyHunk(target, moves)?.Bucket);
    }

    [Fact]
    public void Detect_MovedMethodWithOneEditedLine_LeavesTheEditOutsideTheMove()
    {
        string[] edited = [Method[0], Method[1], "        var fee = amount * FeeRate * 2;", Method[3], Method[4]];
        var target = Added("a1", 12, [.. edited, "    // trailing helper note", "    // second note line"]);
        var source = Removed("r1", 40, [.. Method, "    // trailing helper note", "    // second note line"]);

        var moves = MoveDetector.Detect(Diff(File("A.cs", source), File("B.cs", target)));

        var spans = moves.SpansFor("a1");
        Assert.DoesNotContain(spans, span => span.StartIndex <= 2 && span.StartIndex + span.LineCount > 2);
        Assert.False(moves.IsFullyMoved(target));
        Assert.Null(RuleClassifier.ClassifyHunk(target, moves));
    }

    [Fact]
    public void Detect_OnlyBracesAndBlankLines_IsNotAMove()
    {
        string[] trivial = ["    {", "", "    }", "    }"];

        var moves = MoveDetector.Detect(Diff(File("A.cs", Removed("r1", 10, trivial)), File("B.cs", Added("a1", 20, trivial))));

        Assert.Equal(0, moves.BlockCount);
    }

    [Fact]
    public void Detect_TwoMatchingLines_IsBelowTheMinimumBlockSize()
    {
        string[] twoLines = ["var total = Sum(items);", "Log(total);"];

        var moves = MoveDetector.Detect(Diff(File("A.cs", Removed("r1", 10, twoLines)), File("B.cs", Added("a1", 20, twoLines))));

        Assert.Equal(0, moves.BlockCount);
    }

    [Fact]
    public void Detect_RemovedAndReAddedInTheSameHunk_IsAnInPlaceEditNotAMove()
    {
        var lines = Method.Select(text => new DiffLine(DiffLineKind.Removed, text, 1, null))
            .Concat(Method.Select(text => new DiffLine(DiffLineKind.Added, text + " ", null, 1)))
            .ToList();
        var hunk = new DiffHunk("h1", 1, 5, 1, 5, "", lines);

        Assert.Equal(0, MoveDetector.Detect(Diff(File("A.cs", hunk))).BlockCount);
    }

    [Fact]
    public void Detect_SameBlockPastedTwice_MatchesTheSourceOnlyOnce()
    {
        var source = Removed("r1", 40, Method);
        var firstPaste = Added("a1", 10, Method);
        var secondPaste = Added("a2", 90, Method);

        var moves = MoveDetector.Detect(Diff(File("A.cs", source), File("B.cs", firstPaste, secondPaste)));

        Assert.Single(moves.SpansFor("a1"));
        Assert.Empty(moves.SpansFor("a2"));
        Assert.Equal(1, moves.BlockCount);
    }

    [Fact]
    public void Detect_ClosingMarkupRepeatedAcrossViews_IsNotAMove()
    {
        string[] closingMarkup = ["        </div>", "      </div>", "    </div>", "  </section>"];

        var moves = MoveDetector.Detect(Diff(File("Old.cshtml", Removed("r1", 10, closingMarkup)), File("New.cshtml", Added("a1", 20, closingMarkup))));

        Assert.Equal(0, moves.BlockCount);
    }

    [Fact]
    public void Detect_UsingBlockRemovedFromManyFiles_IsNotTreatedAsMovedIntoOne()
    {
        string[] usings = ["using System;", "using System.Collections.Generic;", "using System.Linq;"];
        var removedEverywhere = Enumerable.Range(0, 5).Select(index => File($"Old{index}.cs", Removed($"r{index}", 1, usings)));

        var moves = MoveDetector.Detect(Diff([.. removedEverywhere, File("New.cs", Added("a1", 1, usings))]));

        Assert.Equal(0, moves.BlockCount);
    }
}
