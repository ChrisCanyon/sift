using System.Text.RegularExpressions;
using Sift.Core.Diff;

namespace Sift.Core.Analysis;

public enum MoveDirection
{
    /// <summary>Added lines that came from elsewhere in the diff.</summary>
    From,

    /// <summary>Removed lines that reappear elsewhere in the diff.</summary>
    To,
}

/// <summary>A run of lines within one hunk that was moved; Other* points at the matching run.</summary>
public sealed record MoveSpan(string BlockId, int StartIndex, int LineCount, MoveDirection Direction, string OtherPath, int OtherLine);

public sealed class MoveMap
{
    public static readonly MoveMap Empty = new(new Dictionary<string, List<MoveSpan>>());

    private readonly Dictionary<string, List<MoveSpan>> spansByHunkId;

    internal MoveMap(Dictionary<string, List<MoveSpan>> spansByHunkId)
    {
        this.spansByHunkId = spansByHunkId;
    }

    public int BlockCount => spansByHunkId.Values.Sum(spans => spans.Count) / 2;

    public IReadOnlyList<MoveSpan> SpansFor(string hunkId) =>
        spansByHunkId.TryGetValue(hunkId, out var spans) ? spans : [];

    /// <summary>Every changed line of the hunk is part of a move.</summary>
    public bool IsFullyMoved(DiffHunk hunk)
    {
        var spans = SpansFor(hunk.Id);
        if (spans.Count == 0)
        {
            return false;
        }

        var movedIndexes = spans.SelectMany(span => Enumerable.Range(span.StartIndex, span.LineCount)).ToHashSet();
        return hunk.Lines
            .Select((line, index) => (line, index))
            .Where(entry => entry.line.Kind != DiffLineKind.Context)
            .All(entry => movedIndexes.Contains(entry.index));
    }
}

/// <summary>Finds code that was cut from one place and pasted unchanged into another, across hunks and files.</summary>
public static partial class MoveDetector
{
    public const int MinimumBlockLines = 3;
    private const int MinimumSignificantLines = 2;

    // Same threshold git uses for --color-moved, so closing markup like "</div></div></div>" doesn't count.
    private const int MinimumBlockAlphanumericCount = 20;

    // Lines removed this often across the diff ("using System;") are boilerplate, not evidence of a move.
    private const int UbiquitousLineCount = 5;

    [GeneratedRegex(@"[A-Za-z0-9]")]
    private static partial Regex AlphanumericPattern();

    private sealed record ChangedLine(string Path, string HunkId, int LineIndex, int LineNumber, int RunId, string Normalized);

    public static MoveMap Detect(DiffResult diff)
    {
        var removedLines = new List<ChangedLine>();
        var addedRuns = new List<List<ChangedLine>>();
        var runId = 0;

        foreach (var file in diff.Files)
        {
            foreach (var hunk in file.Hunks)
            {
                DiffLineKind? previousKind = null;
                List<ChangedLine>? currentAddedRun = null;
                for (var lineIndex = 0; lineIndex < hunk.Lines.Count; lineIndex++)
                {
                    var line = hunk.Lines[lineIndex];
                    if (line.Kind != previousKind)
                    {
                        runId++;
                        currentAddedRun = null;
                    }

                    previousKind = line.Kind;
                    if (line.Kind == DiffLineKind.Removed)
                    {
                        removedLines.Add(new ChangedLine(file.Path, hunk.Id, lineIndex, line.OldLineNumber ?? 0, runId, Normalize(line.Text)));
                    }
                    else if (line.Kind == DiffLineKind.Added)
                    {
                        if (currentAddedRun is null)
                        {
                            currentAddedRun = [];
                            addedRuns.Add(currentAddedRun);
                        }

                        currentAddedRun.Add(new ChangedLine(file.Path, hunk.Id, lineIndex, line.NewLineNumber ?? 0, runId, Normalize(line.Text)));
                    }
                }
            }
        }

        var removedCountByText = removedLines
            .GroupBy(line => line.Normalized, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        bool IsSignificant(string normalized) =>
            IsSubstantive(normalized) && removedCountByText.GetValueOrDefault(normalized) < UbiquitousLineCount;

        var removedPositionsByText = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var position = 0; position < removedLines.Count; position++)
        {
            if (!IsSignificant(removedLines[position].Normalized))
            {
                continue;
            }

            if (!removedPositionsByText.TryGetValue(removedLines[position].Normalized, out var positions))
            {
                removedPositionsByText[removedLines[position].Normalized] = positions = [];
            }

            positions.Add(position);
        }

        var usedRemovedPositions = new HashSet<int>();
        var spansByHunkId = new Dictionary<string, List<MoveSpan>>(StringComparer.Ordinal);
        var blockNumber = 0;

        foreach (var addedRun in addedRuns)
        {
            var addedOffset = 0;
            while (addedOffset < addedRun.Count)
            {
                var (bestStart, bestLength) = FindLongestMatch(addedRun, addedOffset, removedLines, removedPositionsByText, usedRemovedPositions);
                var block = addedRun.Skip(addedOffset).Take(bestLength).ToList();
                if (bestLength < MinimumBlockLines ||
                    block.Count(line => IsSignificant(line.Normalized)) < MinimumSignificantLines ||
                    block.Sum(line => line.Normalized.Count(char.IsLetterOrDigit)) < MinimumBlockAlphanumericCount)
                {
                    addedOffset++;
                    continue;
                }

                var blockId = $"m{++blockNumber}";
                var firstAdded = addedRun[addedOffset];
                var firstRemoved = removedLines[bestStart];
                AddSpan(spansByHunkId, firstAdded.HunkId, new MoveSpan(blockId, firstAdded.LineIndex, bestLength, MoveDirection.From, firstRemoved.Path, firstRemoved.LineNumber));
                AddSpan(spansByHunkId, firstRemoved.HunkId, new MoveSpan(blockId, firstRemoved.LineIndex, bestLength, MoveDirection.To, firstAdded.Path, firstAdded.LineNumber));
                usedRemovedPositions.UnionWith(Enumerable.Range(bestStart, bestLength));
                addedOffset += bestLength;
            }
        }

        return new MoveMap(spansByHunkId);
    }

    private static (int Start, int Length) FindLongestMatch(
        List<ChangedLine> addedRun,
        int addedOffset,
        List<ChangedLine> removedLines,
        Dictionary<string, List<int>> removedPositionsByText,
        HashSet<int> usedRemovedPositions)
    {
        var first = addedRun[addedOffset];
        if (!removedPositionsByText.TryGetValue(first.Normalized, out var candidates))
        {
            return (-1, 0);
        }

        var bestStart = -1;
        var bestLength = 0;
        foreach (var candidate in candidates)
        {
            // Same hunk means an in-place edit, not a move.
            if (usedRemovedPositions.Contains(candidate) || removedLines[candidate].HunkId == first.HunkId)
            {
                continue;
            }

            var length = 0;
            while (addedOffset + length < addedRun.Count &&
                   candidate + length < removedLines.Count &&
                   removedLines[candidate + length].RunId == removedLines[candidate].RunId &&
                   !usedRemovedPositions.Contains(candidate + length) &&
                   removedLines[candidate + length].Normalized == addedRun[addedOffset + length].Normalized)
            {
                length++;
            }

            if (length > bestLength)
            {
                (bestStart, bestLength) = (candidate, length);
            }
        }

        return (bestStart, bestLength);
    }

    private static void AddSpan(Dictionary<string, List<MoveSpan>> spansByHunkId, string hunkId, MoveSpan span)
    {
        if (!spansByHunkId.TryGetValue(hunkId, out var spans))
        {
            spansByHunkId[hunkId] = spans = [];
        }

        spans.Add(span);
    }

    // "}", "", "return;" can't anchor a move on their own.
    private static bool IsSubstantive(string normalized) =>
        normalized.Length >= 4 && AlphanumericPattern().IsMatch(normalized);

    private static string Normalize(string text) =>
        string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
}
