using Sift.Core.Analysis;
using Sift.Core.Git;

namespace Sift.Core.Diff;

public sealed record HunkIndexEntry(
    string Id,
    string Header,
    int Added,
    int Removed,
    IReadOnlyList<string> Preview,
    string? Pattern = null,
    RuleResult? Rule = null);

public sealed record HunkIndexFile(
    string Path,
    string? OldPath,
    FileChangeKind Change,
    bool Binary,
    int Added,
    int Removed,
    int HunkCount,
    RuleResult? Rule,
    IReadOnlyList<HunkIndexEntry>? Hunks,
    IReadOnlyDictionary<string, int>? PatternHunks,
    int? RuleHunks,
    int? LineEndingChangedLines);

public sealed record HunkIndexPatternSample(string Path, string HunkId, string Header, IReadOnlyList<string> Preview);

public sealed record HunkIndexPattern(
    string Id,
    PatternKind Kind,
    string Description,
    int HunkCount,
    int FileCount,
    RuleResult? Rule,
    HunkIndexPatternSample Sample);

/// <summary>
/// Compact listing of a diff for the AI to classify by id without copying diff content.
/// Compact mode drops hunks already covered by a pattern or rule, leaving counts in their place.
/// </summary>
public sealed record HunkIndex(
    string BaseSha,
    string HeadSha,
    string FromSha,
    DiffBase DiffBase,
    IReadOnlyList<HunkIndexPattern> Patterns,
    IReadOnlyList<HunkIndexFile> Files,
    Reconciliation? Reconciliation)
{
    private const int MaximumPreviewLineLength = 120;
    private const int PreviewLeadingContext = 30;
    private const char CarriageReturn = (char)13;

    public static HunkIndex FromDiff(DiffResult diff, int previewLineCount, bool compact)
    {
        var patterns = PatternDetector.Detect(diff);
        var moves = MoveDetector.Detect(diff);
        var patternIdByHunkId = patterns
            .SelectMany(pattern => pattern.Members.Select(member => (member.HunkId, pattern.Id)))
            .ToDictionary(entry => entry.HunkId, entry => entry.Id, StringComparer.Ordinal);
        var hunksById = diff.Files.SelectMany(file => file.Hunks).ToDictionary(hunk => hunk.Id, StringComparer.Ordinal);
        var fileRulesByPath = diff.Files
            .Select(file => (file.Path, Rule: RuleClassifier.ClassifyFile(file)))
            .Where(entry => entry.Rule is not null)
            .ToDictionary(entry => entry.Path, entry => entry.Rule!, StringComparer.Ordinal);

        var patternEntries = patterns.Select(pattern =>
        {
            var sampleMember = pattern.Members[0];
            var sampleHunk = hunksById[sampleMember.HunkId];
            return new HunkIndexPattern(
                pattern.Id,
                pattern.Kind,
                pattern.Description,
                pattern.Members.Count,
                pattern.FileCount,
                RuleClassifier.ClassifyPattern(pattern) ?? SharedFileRule(pattern, fileRulesByPath),
                new HunkIndexPatternSample(sampleMember.Path, sampleHunk.Id, FormatHeader(sampleHunk), Preview(sampleHunk, Math.Max(previewLineCount, 6))));
        }).ToList();

        var fileEntries = diff.Files.Select(file =>
        {
            var fileRule = RuleClassifier.ClassifyFile(file);
            var entries = new List<HunkIndexEntry>();
            var patternHunkCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var ruleHunkCount = 0;

            foreach (var hunk in file.Hunks)
            {
                var patternId = patternIdByHunkId.GetValueOrDefault(hunk.Id);
                var hunkRule = RuleClassifier.ClassifyHunk(hunk, moves);

                if (compact && patternId is not null)
                {
                    patternHunkCounts[patternId] = patternHunkCounts.GetValueOrDefault(patternId) + 1;
                    continue;
                }

                if (compact && hunkRule is not null)
                {
                    ruleHunkCount++;
                    continue;
                }

                entries.Add(ToEntry(hunk, previewLineCount, patternId, hunkRule));
            }

            var omitHunks = compact && fileRule is not null;
            return new HunkIndexFile(
                file.Path,
                file.OldPath,
                file.ChangeKind,
                file.IsBinary,
                file.AddedLineCount,
                file.RemovedLineCount,
                file.Hunks.Count,
                fileRule,
                omitHunks || entries.Count == 0 ? null : entries,
                patternHunkCounts.Count > 0 ? patternHunkCounts : null,
                ruleHunkCount > 0 ? ruleHunkCount : null,
                file.LineEndingChangedLines > 0 ? file.LineEndingChangedLines : null);
        }).ToList();

        return new HunkIndex(diff.BaseSha, diff.HeadSha, diff.FromSha, diff.DiffBase, patternEntries, fileEntries, diff.Reconciliation);
    }

    // A pattern that only occurs inside rule-classified files (e.g. the same version bump in five lockfiles) needs no judgment.
    private static RuleResult? SharedFileRule(PatternGroup pattern, Dictionary<string, RuleResult> fileRulesByPath)
    {
        var memberRules = pattern.Members.Select(member => fileRulesByPath.GetValueOrDefault(member.Path)).ToList();
        if (memberRules.Any(rule => rule is null))
        {
            return null;
        }

        var first = memberRules[0]!;
        return memberRules.All(rule => rule!.Bucket == first.Bucket)
            ? new RuleResult(first.Bucket, memberRules.All(rule => rule!.Reason == first.Reason) ? $"Only in {first.Reason.ToLowerInvariant()}s" : "Only in rule-classified files")
            : null;
    }

    private static HunkIndexEntry ToEntry(DiffHunk hunk, int previewLineCount, string? patternId, RuleResult? rule)
    {
        var changedLines = hunk.Lines.Where(line => line.Kind != DiffLineKind.Context).ToList();
        return new HunkIndexEntry(
            hunk.Id,
            FormatHeader(hunk),
            changedLines.Count(line => line.Kind == DiffLineKind.Added),
            changedLines.Count(line => line.Kind == DiffLineKind.Removed),
            Preview(hunk, previewLineCount),
            patternId,
            rule);
    }

    private static string FormatHeader(DiffHunk hunk) =>
        $"@@ -{hunk.OldStart},{hunk.OldLineCount} +{hunk.NewStart},{hunk.NewLineCount} @@ {hunk.SectionHeader}".TrimEnd();

    private static List<string> Preview(DiffHunk hunk, int previewLineCount)
    {
        var changedLines = hunk.Lines.Where(line => line.Kind != DiffLineKind.Context).Take(previewLineCount).ToList();
        var startOffsets = new int[changedLines.Count];

        // Long paired lines often differ only near the end; start both previews just before the difference.
        for (var index = 0; index + 1 < changedLines.Count; index++)
        {
            if (changedLines[index].Kind != DiffLineKind.Removed || changedLines[index + 1].Kind != DiffLineKind.Added)
            {
                continue;
            }

            var commonPrefixLength = CommonPrefixLength(changedLines[index].Text, changedLines[index + 1].Text);
            if (commonPrefixLength > MaximumPreviewLineLength - PreviewLeadingContext)
            {
                startOffsets[index] = startOffsets[index + 1] = commonPrefixLength - PreviewLeadingContext;
            }
        }

        return changedLines.Select((line, index) => FormatPreviewLine(line, startOffsets[index])).ToList();
    }

    private static int CommonPrefixLength(string first, string second)
    {
        var length = 0;
        while (length < first.Length && length < second.Length && first[length] == second[length])
        {
            length++;
        }

        return length;
    }

    private static string FormatPreviewLine(DiffLine line, int startOffset)
    {
        var text = line.Text.TrimEnd(CarriageReturn);
        var body = startOffset > 0 ? "…" + text[startOffset..] : text;
        var formatted = (line.Kind == DiffLineKind.Added ? "+" : "-") + body;
        return formatted.Length <= MaximumPreviewLineLength ? formatted : formatted[..MaximumPreviewLineLength] + "…";
    }
}
