using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Sift.Core.Diff;

/// <summary>Parses `git diff` output produced with explicit a/ b/ prefixes.</summary>
public static partial class UnifiedDiffParser
{
    private const string DevNull = "/dev/null";

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@ ?(.*)$")]
    private static partial Regex HunkHeaderPattern();

    public static IReadOnlyList<DiffFile> Parse(string diffOutput)
    {
        var files = new List<DiffFile>();
        var lines = diffOutput.Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0)
        {
            lines = lines[..^1];
        }

        FileBuilder? currentFile = null;
        var lineIndex = 0;

        while (lineIndex < lines.Length)
        {
            var line = lines[lineIndex];

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                if (currentFile is not null)
                {
                    files.Add(currentFile.Build());
                }

                currentFile = new FileBuilder();
                (currentFile.HeaderOldPath, currentFile.HeaderNewPath) = ParseDiffGitHeaderPaths(line["diff --git ".Length..]);
                lineIndex++;
                continue;
            }

            if (currentFile is null)
            {
                lineIndex++;
                continue;
            }

            var hunkMatch = HunkHeaderPattern().Match(line);
            if (hunkMatch.Success)
            {
                lineIndex = ParseHunk(lines, lineIndex, hunkMatch, currentFile);
                continue;
            }

            ParseFileHeaderLine(line, currentFile);
            lineIndex++;
        }

        if (currentFile is not null)
        {
            files.Add(currentFile.Build());
        }

        return files;
    }

    private static void ParseFileHeaderLine(string line, FileBuilder file)
    {
        if (TryStripPrefix(line, "new file mode ", out var newFileMode))
        {
            file.ChangeKind = FileChangeKind.Added;
            file.NewMode = newFileMode;
        }
        else if (TryStripPrefix(line, "deleted file mode ", out var deletedFileMode))
        {
            file.ChangeKind = FileChangeKind.Deleted;
            file.OldMode = deletedFileMode;
        }
        else if (TryStripPrefix(line, "old mode ", out var oldMode))
        {
            file.OldMode = oldMode;
        }
        else if (TryStripPrefix(line, "new mode ", out var newMode))
        {
            file.NewMode = newMode;
        }
        else if (TryStripPrefix(line, "similarity index ", out var similarity))
        {
            if (int.TryParse(similarity.TrimEnd('%'), out var similarityPercent))
            {
                file.SimilarityPercent = similarityPercent;
            }
        }
        else if (TryStripPrefix(line, "rename from ", out var renameFrom))
        {
            file.ChangeKind = FileChangeKind.Renamed;
            file.ExplicitOldPath = UnquoteGitPath(renameFrom);
        }
        else if (TryStripPrefix(line, "rename to ", out var renameTo))
        {
            file.ChangeKind = FileChangeKind.Renamed;
            file.ExplicitNewPath = UnquoteGitPath(renameTo);
        }
        else if (TryStripPrefix(line, "copy from ", out var copyFrom))
        {
            file.ChangeKind = FileChangeKind.Copied;
            file.ExplicitOldPath = UnquoteGitPath(copyFrom);
        }
        else if (TryStripPrefix(line, "copy to ", out var copyTo))
        {
            file.ChangeKind = FileChangeKind.Copied;
            file.ExplicitNewPath = UnquoteGitPath(copyTo);
        }
        else if (TryStripPrefix(line, "--- ", out var oldMarker))
        {
            file.MarkerOldPath = ParseMarkerPath(oldMarker, "a/");
        }
        else if (TryStripPrefix(line, "+++ ", out var newMarker))
        {
            file.MarkerNewPath = ParseMarkerPath(newMarker, "b/");
        }
        else if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line == "GIT binary patch")
        {
            file.IsBinary = true;
        }
    }

    private static int ParseHunk(string[] lines, int headerIndex, Match headerMatch, FileBuilder file)
    {
        var oldStart = int.Parse(headerMatch.Groups[1].Value);
        var oldCount = headerMatch.Groups[2].Success ? int.Parse(headerMatch.Groups[2].Value) : 1;
        var newStart = int.Parse(headerMatch.Groups[3].Value);
        var newCount = headerMatch.Groups[4].Success ? int.Parse(headerMatch.Groups[4].Value) : 1;
        var sectionHeader = headerMatch.Groups[5].Value.TrimEnd('\r');

        var hunkLines = new List<DiffLine>();
        var oldRemaining = oldCount;
        var newRemaining = newCount;
        var oldLineNumber = oldStart;
        var newLineNumber = newStart;
        var lineIndex = headerIndex + 1;

        // Counts, not prefixes, bound the hunk: a removed "-- x" line reads as "--- x".
        while (lineIndex < lines.Length && (oldRemaining > 0 || newRemaining > 0))
        {
            var line = lines[lineIndex];
            var marker = line.Length == 0 ? ' ' : line[0];
            var text = line.Length == 0 ? string.Empty : line[1..];

            switch (marker)
            {
                case '+':
                    hunkLines.Add(new DiffLine(DiffLineKind.Added, text, null, newLineNumber++));
                    newRemaining--;
                    break;
                case '-':
                    hunkLines.Add(new DiffLine(DiffLineKind.Removed, text, oldLineNumber++, null));
                    oldRemaining--;
                    break;
                case '\\':
                    MarkNoNewlineAtEnd(hunkLines);
                    break;
                default:
                    hunkLines.Add(new DiffLine(DiffLineKind.Context, text, oldLineNumber++, newLineNumber++));
                    oldRemaining--;
                    newRemaining--;
                    break;
            }

            lineIndex++;
        }

        while (lineIndex < lines.Length && lines[lineIndex].StartsWith('\\'))
        {
            MarkNoNewlineAtEnd(hunkLines);
            lineIndex++;
        }

        file.Hunks.Add(new PendingHunk(oldStart, oldCount, newStart, newCount, sectionHeader, hunkLines));
        return lineIndex;
    }

    private static void MarkNoNewlineAtEnd(List<DiffLine> hunkLines)
    {
        if (hunkLines.Count > 0)
        {
            hunkLines[^1] = hunkLines[^1] with { NoNewlineAtEnd = true };
        }
    }

    private static bool TryStripPrefix(string line, string prefix, out string remainder)
    {
        if (line.StartsWith(prefix, StringComparison.Ordinal))
        {
            remainder = line[prefix.Length..].TrimEnd('\r');
            return true;
        }

        remainder = string.Empty;
        return false;
    }

    private static string? ParseMarkerPath(string markerValue, string expectedPrefix)
    {
        // Git appends a tab to marker paths containing spaces.
        var path = UnquoteGitPath(markerValue.TrimEnd('\t'));
        if (path == DevNull)
        {
            return null;
        }

        return StripPrefix(path, expectedPrefix);
    }

    private static (string? OldPath, string? NewPath) ParseDiffGitHeaderPaths(string headerPaths)
    {
        headerPaths = headerPaths.TrimEnd('\r');

        if (headerPaths.StartsWith('"'))
        {
            var oldEnd = FindClosingQuote(headerPaths, 0);
            if (oldEnd < 0)
            {
                return (null, null);
            }

            var oldToken = headerPaths[..(oldEnd + 1)];
            var newToken = headerPaths[(oldEnd + 1)..].TrimStart();
            return (StripPrefix(UnquoteGitPath(oldToken), "a/"), StripPrefix(UnquoteGitPath(newToken), "b/"));
        }

        if (headerPaths.EndsWith('"'))
        {
            var newStart = headerPaths.LastIndexOf(" \"", StringComparison.Ordinal);
            if (newStart < 0)
            {
                return (null, null);
            }

            return (StripPrefix(headerPaths[..newStart], "a/"), StripPrefix(UnquoteGitPath(headerPaths[(newStart + 1)..]), "b/"));
        }

        // Unquoted and unrenamed: "a/X b/X", so X is exactly half the remaining length.
        if (headerPaths.StartsWith("a/", StringComparison.Ordinal) && (headerPaths.Length - 5) % 2 == 0)
        {
            var pathLength = (headerPaths.Length - 5) / 2;
            var oldPath = headerPaths.Substring(2, pathLength);
            if (headerPaths[(2 + pathLength)..] == " b/" + oldPath)
            {
                return (oldPath, oldPath);
            }
        }

        var separator = headerPaths.IndexOf(" b/", StringComparison.Ordinal);
        return separator < 0
            ? (null, null)
            : (StripPrefix(headerPaths[..separator], "a/"), headerPaths[(separator + 3)..]);
    }

    private static string StripPrefix(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;

    private static int FindClosingQuote(string value, int openingQuoteIndex)
    {
        for (var index = openingQuoteIndex + 1; index < value.Length; index++)
        {
            if (value[index] == '\\')
            {
                index++;
            }
            else if (value[index] == '"')
            {
                return index;
            }
        }

        return -1;
    }

    internal static string UnquoteGitPath(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            return value;
        }

        var bytes = new List<byte>();
        var inner = value[1..^1];
        for (var index = 0; index < inner.Length; index++)
        {
            var character = inner[index];
            if (character != '\\' || index + 1 >= inner.Length)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(character.ToString()));
                continue;
            }

            var escaped = inner[++index];
            if (escaped is >= '0' and <= '7' && index + 2 < inner.Length)
            {
                bytes.Add(Convert.ToByte(inner.Substring(index, 3), 8));
                index += 2;
                continue;
            }

            bytes.Add(escaped switch
            {
                'a' => 0x07,
                'b' => 0x08,
                't' => 0x09,
                'n' => 0x0A,
                'v' => 0x0B,
                'f' => 0x0C,
                'r' => 0x0D,
                _ => (byte)escaped,
            });
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private sealed record PendingHunk(
        int OldStart,
        int OldLineCount,
        int NewStart,
        int NewLineCount,
        string SectionHeader,
        IReadOnlyList<DiffLine> Lines);

    private sealed class FileBuilder
    {
        public string? HeaderOldPath { get; set; }
        public string? HeaderNewPath { get; set; }
        public string? ExplicitOldPath { get; set; }
        public string? ExplicitNewPath { get; set; }
        public string? MarkerOldPath { get; set; }
        public string? MarkerNewPath { get; set; }
        public FileChangeKind ChangeKind { get; set; } = FileChangeKind.Modified;
        public bool IsBinary { get; set; }
        public int? SimilarityPercent { get; set; }
        public string? OldMode { get; set; }
        public string? NewMode { get; set; }
        public List<PendingHunk> Hunks { get; } = [];

        public DiffFile Build()
        {
            var oldPath = ExplicitOldPath ?? MarkerOldPath ?? HeaderOldPath;
            var newPath = ExplicitNewPath ?? MarkerNewPath ?? HeaderNewPath;
            var path = (ChangeKind == FileChangeKind.Deleted ? oldPath ?? newPath : newPath ?? oldPath) ?? string.Empty;
            var hasDistinctOldPath = ChangeKind is FileChangeKind.Renamed or FileChangeKind.Copied;

            return new DiffFile(
                path,
                hasDistinctOldPath ? oldPath : null,
                ChangeKind,
                IsBinary,
                SimilarityPercent,
                OldMode,
                NewMode,
                BuildHunksWithStableIds(path));
        }

        private List<DiffHunk> BuildHunksWithStableIds(string path)
        {
            var occurrencesBySignature = new Dictionary<string, int>(StringComparer.Ordinal);
            var hunks = new List<DiffHunk>(Hunks.Count);

            foreach (var pending in Hunks)
            {
                var signature = BuildChangeSignature(pending.Lines);
                occurrencesBySignature.TryGetValue(signature, out var occurrence);
                occurrencesBySignature[signature] = occurrence + 1;

                hunks.Add(new DiffHunk(
                    ComputeHunkId(path, signature, occurrence),
                    pending.OldStart,
                    pending.OldLineCount,
                    pending.NewStart,
                    pending.NewLineCount,
                    pending.SectionHeader,
                    pending.Lines));
            }

            return hunks;
        }

        // Only changed lines feed the signature, so ids survive line shifts from unrelated edits.
        private static string BuildChangeSignature(IReadOnlyList<DiffLine> lines)
        {
            var builder = new StringBuilder();
            foreach (var line in lines)
            {
                if (line.Kind == DiffLineKind.Context)
                {
                    continue;
                }

                builder.Append(line.Kind == DiffLineKind.Added ? '+' : '-').Append(line.Text).Append('\n');
            }

            return builder.ToString();
        }

        private static string ComputeHunkId(string path, string signature, int occurrence)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{path}\0{occurrence}\0{signature}"));
            return Convert.ToHexStringLower(hash)[..16];
        }
    }
}
