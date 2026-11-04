using Sift.Core.Analysis;
using Sift.Core.Diff;

namespace Sift.Core.Review;

/// <summary>Combines AI classifications, deterministic rules and pattern groups with the real diff into ordered buckets.</summary>
public static class ReviewAssembler
{
    public const string UnsortedBucketId = "unsorted";

    public static readonly IReadOnlyList<ReviewBucket> DefaultBuckets =
    [
        new("core", "Core logic", 1),
        new("contract", "API / contract / schema", 2),
        new("config", "Config & infrastructure", 3),
        new("tests", "Tests", 4),
        new("refactor", "Mechanical refactor & renames", 5, Collapsed: true),
        new("docs", "Documentation", 6, Collapsed: true),
        new("noise", "Generated, lockfiles, formatting", 7, Collapsed: true),
    ];

    private static readonly ReviewBucket UnsortedBucket =
        new(UnsortedBucketId, "Unsorted: needs classification", int.MinValue);

    private sealed record Placement(string BucketId, Priority Priority, Classifier ClassifiedBy, string? Reason, string? Risk)
    {
        public static readonly Placement Unsorted = new(UnsortedBucketId, Priority.Normal, Classifier.None, null, null);

        public static Placement FromAi(string bucket, Priority? priority, string? reason, string? risk) =>
            new(bucket, priority ?? Priority.Normal, Classifier.Ai, reason, risk);

        public static Placement FromRule(RuleResult rule) =>
            new(rule.Bucket, Priority.Low, Classifier.Rule, rule.Reason, null);

        public bool IsIn(string bucketId) => string.Equals(BucketId, bucketId, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class BucketContents
    {
        public List<FileView> Files { get; } = [];
        public List<PatternView> Patterns { get; } = [];
    }

    public static ReviewView Assemble(ReviewDocument document, DiffResult diff)
    {
        var bucketsById = (document.Buckets is { Count: > 0 } ? document.Buckets : DefaultBuckets)
            .ToDictionary(bucket => bucket.Id, StringComparer.OrdinalIgnoreCase);
        var fileClassificationsByPath = LastById(document.Files, classification => classification.Path);
        var hunkClassificationsById = LastById(document.Hunks, classification => classification.Id);
        var patternClassificationsById = LastById(document.Patterns, classification => classification.Id);

        var patterns = PatternDetector.Detect(diff);
        var moves = MoveDetector.Detect(diff);
        var patternByHunkId = patterns
            .SelectMany(pattern => pattern.Members.Select(member => (member.HunkId, Pattern: pattern)))
            .ToDictionary(entry => entry.HunkId, entry => entry.Pattern, StringComparer.Ordinal);

        var matchedFilePaths = new HashSet<string>(StringComparer.Ordinal);
        var contentsByBucket = new Dictionary<string, BucketContents>(StringComparer.OrdinalIgnoreCase);
        var patternMembers = new Dictionary<string, List<(string Path, DiffHunk Hunk, Placement Fallback)>>(StringComparer.Ordinal);

        foreach (var file in diff.Files)
        {
            var fileClassification = FindFileClassification(file, fileClassificationsByPath);
            if (fileClassification is not null)
            {
                matchedFilePaths.Add(fileClassification.Path);
            }

            var fileRule = RuleClassifier.ClassifyFile(file);
            var filePlacement = fileClassification is not null
                ? Placement.FromAi(fileClassification.Bucket, fileClassification.Priority, fileClassification.Reason, fileClassification.Risk)
                : fileRule is not null ? Placement.FromRule(fileRule) : null;

            if (file.Hunks.Count == 0)
            {
                var placement = filePlacement ?? Placement.Unsorted;
                Contents(contentsByBucket, placement.BucketId).Files.Add(ToFileView(file, placement, placement.ClassifiedBy, []));
                continue;
            }

            var hunkViewsByBucket = new Dictionary<string, List<HunkView>>(StringComparer.OrdinalIgnoreCase);
            foreach (var hunk in file.Hunks)
            {
                if (hunkClassificationsById.TryGetValue(hunk.Id, out var hunkClassification))
                {
                    var aiPlacement = Placement.FromAi(
                        hunkClassification.Bucket,
                        hunkClassification.Priority ?? filePlacement?.Priority,
                        hunkClassification.Reason,
                        hunkClassification.Risk);
                    AddHunk(hunkViewsByBucket, hunk, aiPlacement, moves);
                    continue;
                }

                var hunkRule = RuleClassifier.ClassifyHunk(hunk, moves);
                var fallback = hunkRule is not null
                    ? Placement.FromRule(hunkRule)
                    : filePlacement is not null ? filePlacement with { Reason = null, Risk = null } : Placement.Unsorted;

                if (patternByHunkId.TryGetValue(hunk.Id, out var pattern))
                {
                    if (!patternMembers.TryGetValue(pattern.Id, out var members))
                    {
                        patternMembers[pattern.Id] = members = [];
                    }

                    members.Add((file.Path, hunk, fallback));
                    continue;
                }

                AddHunk(hunkViewsByBucket, hunk, fallback, moves);
            }

            foreach (var (bucketId, hunkViews) in hunkViewsByBucket)
            {
                var ownsFileNotes = filePlacement is not null && filePlacement.IsIn(bucketId);
                var placement = new Placement(
                    bucketId,
                    hunkViews.Min(hunkView => hunkView.Priority),
                    ownsFileNotes ? filePlacement!.ClassifiedBy : hunkViews[0].ClassifiedBy,
                    ownsFileNotes ? filePlacement!.Reason : null,
                    ownsFileNotes ? filePlacement!.Risk : null);
                Contents(contentsByBucket, bucketId).Files.Add(ToFileView(file, placement, placement.ClassifiedBy, hunkViews));
            }
        }

        foreach (var pattern in patterns)
        {
            if (!patternMembers.TryGetValue(pattern.Id, out var members) || members.Count == 0)
            {
                continue;
            }

            var placement = ResolvePatternPlacement(pattern, members.Select(member => member.Fallback).ToList(), patternClassificationsById);
            var memberViews = members
                .Select(member => new PatternMemberView(member.Path, ToHunkView(member.Hunk, placement with { Reason = null, Risk = null }, moves)))
                .ToList();

            Contents(contentsByBucket, placement.BucketId).Patterns.Add(new PatternView(
                pattern.Id,
                pattern.Kind,
                pattern.Description,
                placement.Priority,
                placement.ClassifiedBy,
                placement.Reason,
                placement.Risk,
                memberViews.Select(member => member.Path).Distinct(StringComparer.Ordinal).Count(),
                memberViews));
        }

        var buckets = contentsByBucket
            .Select(entry => (Bucket: ResolveBucket(entry.Key, bucketsById), entry.Value))
            .OrderBy(entry => entry.Bucket.Order)
            .ThenBy(entry => entry.Bucket.Id, StringComparer.Ordinal)
            .Select(entry => new BucketView(
                entry.Bucket.Id,
                entry.Bucket.Title,
                entry.Bucket.Description,
                entry.Bucket.Collapsed,
                entry.Value.Files.Sum(file => Math.Max(file.Hunks.Count, 1)) + entry.Value.Patterns.Sum(pattern => pattern.Members.Count),
                entry.Value.Patterns.OrderBy(pattern => pattern.Priority).ThenByDescending(pattern => pattern.Members.Count).ToList(),
                entry.Value.Files.OrderBy(file => file.Priority).ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();

        var allHunkIds = diff.Files.SelectMany(file => file.Hunks).Select(hunk => hunk.Id).ToHashSet(StringComparer.Ordinal);
        var allPatternIds = patterns.Select(pattern => pattern.Id).ToHashSet(StringComparer.Ordinal);

        var placementProblems = FindPlacementProblems(diff, buckets);
        var reconciliation = diff.Reconciliation?.WithProblems(placementProblems)
            ?? new Reconciliation(diff.Files.Count, diff.Files.Sum(file => file.AddedLineCount), diff.Files.Sum(file => file.RemovedLineCount), placementProblems);

        return new ReviewView(
            document.Title,
            document.Summary,
            document.Source,
            diff.FromSha,
            buckets,
            document.Threads ?? [],
            hunkClassificationsById.Keys.Where(id => !allHunkIds.Contains(id)).Order(StringComparer.Ordinal).ToList(),
            fileClassificationsByPath.Keys.Where(path => !matchedFilePaths.Contains(path)).Order(StringComparer.Ordinal).ToList(),
            patternClassificationsById.Keys.Where(id => !allPatternIds.Contains(id)).Order(StringComparer.Ordinal).ToList(),
            reconciliation);
    }

    /// <summary>Every hunk must appear exactly once across buckets and patterns; every hunkless file exactly once.</summary>
    private static List<ReconciliationProblem> FindPlacementProblems(DiffResult diff, IReadOnlyList<BucketView> buckets)
    {
        var placedHunkCounts = buckets
            .SelectMany(bucket => bucket.Files.SelectMany(file => file.Hunks.Select(hunk => hunk.Id))
                .Concat(bucket.Patterns.SelectMany(pattern => pattern.Members.Select(member => member.Hunk.Id))))
            .GroupBy(id => id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var placedHunklessFileCounts = buckets
            .SelectMany(bucket => bucket.Files.Where(file => file.Hunks.Count == 0).Select(file => file.Path))
            .GroupBy(path => path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var problems = new List<ReconciliationProblem>();
        foreach (var file in diff.Files)
        {
            if (file.Hunks.Count == 0)
            {
                var count = placedHunklessFileCounts.GetValueOrDefault(file.Path);
                if (count != 1)
                {
                    problems.Add(new ReconciliationProblem(ReconciliationProblemKind.NotPlaced, file.Path, $"File shown {count} times"));
                }

                continue;
            }

            foreach (var hunk in file.Hunks)
            {
                var count = placedHunkCounts.GetValueOrDefault(hunk.Id);
                if (count != 1)
                {
                    problems.Add(new ReconciliationProblem(ReconciliationProblemKind.NotPlaced, file.Path, $"Hunk {hunk.Id} shown {count} times"));
                }
            }
        }

        return problems;
    }

    private static Placement ResolvePatternPlacement(
        PatternGroup pattern,
        IReadOnlyList<Placement> memberFallbacks,
        Dictionary<string, PatternClassification> patternClassificationsById)
    {
        if (patternClassificationsById.TryGetValue(pattern.Id, out var classification))
        {
            return Placement.FromAi(classification.Bucket, classification.Priority, classification.Reason, classification.Risk);
        }

        if (RuleClassifier.ClassifyPattern(pattern) is { } rule)
        {
            return Placement.FromRule(rule);
        }

        var first = memberFallbacks[0];
        return memberFallbacks.All(fallback => fallback.IsIn(first.BucketId))
            ? first with { Priority = memberFallbacks.Min(fallback => fallback.Priority) }
            : Placement.Unsorted;
    }

    private static Dictionary<string, T> LastById<T>(IReadOnlyList<T>? items, Func<T, string> keySelector) =>
        (items ?? [])
            .GroupBy(keySelector, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

    private static FileClassification? FindFileClassification(DiffFile file, Dictionary<string, FileClassification> fileClassificationsByPath)
    {
        if (fileClassificationsByPath.TryGetValue(file.Path, out var classification))
        {
            return classification;
        }

        return file.OldPath is not null && fileClassificationsByPath.TryGetValue(file.OldPath, out classification)
            ? classification
            : null;
    }

    // An unknown bucket id from the AI still gets shown rather than dropping its hunks.
    private static ReviewBucket ResolveBucket(string bucketId, Dictionary<string, ReviewBucket> bucketsById)
    {
        if (string.Equals(bucketId, UnsortedBucketId, StringComparison.OrdinalIgnoreCase))
        {
            return UnsortedBucket;
        }

        if (bucketsById.TryGetValue(bucketId, out var bucket))
        {
            return bucket;
        }

        var defaultBucket = DefaultBuckets.FirstOrDefault(candidate => string.Equals(candidate.Id, bucketId, StringComparison.OrdinalIgnoreCase));
        return defaultBucket ?? new ReviewBucket(bucketId, bucketId, int.MaxValue);
    }

    private static BucketContents Contents(Dictionary<string, BucketContents> contentsByBucket, string bucketId)
    {
        if (!contentsByBucket.TryGetValue(bucketId, out var contents))
        {
            contentsByBucket[bucketId] = contents = new BucketContents();
        }

        return contents;
    }

    private static void AddHunk(Dictionary<string, List<HunkView>> hunkViewsByBucket, DiffHunk hunk, Placement placement, MoveMap moves)
    {
        if (!hunkViewsByBucket.TryGetValue(placement.BucketId, out var hunkViews))
        {
            hunkViewsByBucket[placement.BucketId] = hunkViews = [];
        }

        hunkViews.Add(ToHunkView(hunk, placement, moves));
    }

    private static FileView ToFileView(DiffFile file, Placement placement, Classifier classifiedBy, IReadOnlyList<HunkView> hunks) => new(
        file.Path,
        file.OldPath,
        file.ChangeKind,
        file.IsBinary,
        file.AddedLineCount,
        file.RemovedLineCount,
        placement.Priority,
        classifiedBy,
        placement.Reason,
        placement.Risk,
        file.Hunks.Count,
        file.LineEndingChangedLines,
        hunks);

    private static HunkView ToHunkView(DiffHunk hunk, Placement placement, MoveMap moves) => new(
        hunk.Id,
        hunk.OldStart,
        hunk.OldLineCount,
        hunk.NewStart,
        hunk.NewLineCount,
        hunk.SectionHeader,
        placement.Priority,
        placement.ClassifiedBy,
        placement.Reason,
        placement.Risk,
        hunk.Lines,
        moves.SpansFor(hunk.Id));
}
