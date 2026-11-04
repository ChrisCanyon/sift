using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Sift.Core.Diff;

namespace Sift.Core.Analysis;

public enum PatternKind
{
    /// <summary>Every changed line is the same token replacement, e.g. a rename.</summary>
    Replacement,

    /// <summary>The same lines added or removed in several places.</summary>
    RepeatedLines,
}

public sealed record PatternMember(string Path, string HunkId);

public sealed record PatternGroup(
    string Id,
    PatternKind Kind,
    string Description,
    bool IsIdentifierReplacement,
    IReadOnlyList<PatternMember> Members)
{
    public int FileCount => Members.Select(member => member.Path).Distinct(StringComparer.Ordinal).Count();
}

/// <summary>Finds the same change applied across many hunks so it can be reviewed once.</summary>
public static partial class PatternDetector
{
    public const int MinimumOccurrences = 3;
    private const int MaximumDescribedReplacements = 3;
    private const int MaximumDescriptionLength = 140;

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*|\d+(?:\.\d+)?|>>=|<<=|\?\?=|[=!]==|[-+*/%&|^<>!=?:]=|=>|->|&&|\|\||\?\?|::|\+\+|--|<<|>>|\S")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierPattern();

    private sealed record Replacement(string OldText, string NewText)
    {
        public override string ToString() => $"{Display(OldText)} → {Display(NewText)}";

        private static string Display(string text) => text.Length == 0 ? "∅" : text;
    }

    private sealed record HunkSignature(PatternKind Kind, string Key, string Description, bool IsIdentifierReplacement);

    public static IReadOnlyList<PatternGroup> Detect(DiffResult diff)
    {
        var membersBySignature = new Dictionary<string, (HunkSignature Signature, List<PatternMember> Members)>(StringComparer.Ordinal);

        foreach (var file in diff.Files)
        {
            foreach (var hunk in file.Hunks)
            {
                var signature = ComputeSignature(hunk);
                if (signature is null)
                {
                    continue;
                }

                if (!membersBySignature.TryGetValue(signature.Key, out var entry))
                {
                    membersBySignature[signature.Key] = entry = (signature, []);
                }

                entry.Members.Add(new PatternMember(file.Path, hunk.Id));
            }
        }

        return membersBySignature.Values
            .Where(entry => entry.Members.Count >= MinimumOccurrences)
            .Select(entry => new PatternGroup(
                ComputePatternId(entry.Signature.Key),
                entry.Signature.Kind,
                entry.Signature.Description,
                entry.Signature.IsIdentifierReplacement,
                entry.Members))
            .OrderByDescending(group => group.Members.Count)
            .ThenBy(group => group.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>True when every removed/added pair differs only in whitespace.</summary>
    public static bool IsWhitespaceOnly(DiffHunk hunk)
    {
        var removed = string.Concat(hunk.Lines.Where(line => line.Kind == DiffLineKind.Removed).Select(line => StripWhitespace(line.Text)));
        var added = string.Concat(hunk.Lines.Where(line => line.Kind == DiffLineKind.Added).Select(line => StripWhitespace(line.Text)));
        return hunk.Lines.Any(line => line.Kind != DiffLineKind.Context) && removed == added;
    }

    private static HunkSignature? ComputeSignature(DiffHunk hunk)
    {
        if (IsWhitespaceOnly(hunk))
        {
            return null;
        }

        var blocks = SplitIntoChangeBlocks(hunk.Lines);
        var allBlocksArePaired = blocks.All(block => block.Removed.Count > 0 && block.Removed.Count == block.Added.Count);

        if (allBlocksArePaired)
        {
            var replacements = new SortedSet<string>(StringComparer.Ordinal);
            var replacementDetails = new List<Replacement>();
            foreach (var block in blocks)
            {
                for (var pairIndex = 0; pairIndex < block.Removed.Count; pairIndex++)
                {
                    foreach (var replacement in FindReplacements(block.Removed[pairIndex], block.Added[pairIndex]))
                    {
                        if (replacements.Add(replacement.ToString()))
                        {
                            replacementDetails.Add(replacement);
                        }
                    }
                }
            }

            if (replacements.Count > 0)
            {
                var isIdentifierReplacement = replacementDetails.All(replacement =>
                    IsQualifiedName(replacement.OldText) && IsQualifiedName(replacement.NewText));
                var described = replacements.Take(MaximumDescribedReplacements).ToList();
                var description = string.Join(", ", described) +
                    (replacements.Count > described.Count ? $" (+{replacements.Count - described.Count} more)" : "");

                return new HunkSignature(PatternKind.Replacement, "R\n" + string.Join('\n', replacements), Truncate(description), isIdentifierReplacement);
            }
        }

        var changedLines = hunk.Lines
            .Where(line => line.Kind != DiffLineKind.Context)
            .Select(line => (line.Kind == DiffLineKind.Added ? "+" : "-") + line.Text.Trim())
            .ToList();
        var firstLine = changedLines[0];
        var lineDescription = changedLines.Count == 1 ? firstLine : $"{firstLine} (+{changedLines.Count - 1} lines)";

        return new HunkSignature(PatternKind.RepeatedLines, "L\n" + string.Join('\n', changedLines), Truncate(lineDescription), false);
    }

    private sealed record ChangeBlock(List<string> Removed, List<string> Added);

    private static List<ChangeBlock> SplitIntoChangeBlocks(IReadOnlyList<DiffLine> lines)
    {
        var blocks = new List<ChangeBlock>();
        ChangeBlock? current = null;

        foreach (var line in lines)
        {
            if (line.Kind == DiffLineKind.Context)
            {
                current = null;
                continue;
            }

            // A removal after additions starts a new block: "-a +b -c +d" pairs a/b and c/d.
            if (current is null || line.Kind == DiffLineKind.Removed && current.Added.Count > 0)
            {
                current = new ChangeBlock([], []);
                blocks.Add(current);
            }

            (line.Kind == DiffLineKind.Removed ? current.Removed : current.Added).Add(line.Text.TrimEnd('\r'));
        }

        return blocks;
    }

    private static IEnumerable<Replacement> FindReplacements(string oldLine, string newLine)
    {
        var oldTokens = TokenPattern().Matches(oldLine);
        var newTokens = TokenPattern().Matches(newLine);

        if (oldTokens.Count == newTokens.Count)
        {
            // Positional: adjacent differing tokens merge into one span, so "A.B" → "C.D" stays whole.
            var index = 0;
            while (index < oldTokens.Count)
            {
                if (oldTokens[index].Value == newTokens[index].Value)
                {
                    index++;
                    continue;
                }

                var start = index;
                while (index < oldTokens.Count && oldTokens[index].Value != newTokens[index].Value)
                {
                    index++;
                }

                yield return new Replacement(Slice(oldLine, oldTokens, start, index), Slice(newLine, newTokens, start, index));
            }

            yield break;
        }

        var prefixLength = 0;
        while (prefixLength < oldTokens.Count && prefixLength < newTokens.Count && oldTokens[prefixLength].Value == newTokens[prefixLength].Value)
        {
            prefixLength++;
        }

        var suffixLength = 0;
        while (suffixLength < oldTokens.Count - prefixLength &&
               suffixLength < newTokens.Count - prefixLength &&
               oldTokens[oldTokens.Count - suffixLength - 1].Value == newTokens[newTokens.Count - suffixLength - 1].Value)
        {
            suffixLength++;
        }

        yield return new Replacement(
            Slice(oldLine, oldTokens, prefixLength, oldTokens.Count - suffixLength),
            Slice(newLine, newTokens, prefixLength, newTokens.Count - suffixLength));
    }

    private static string Slice(string line, MatchCollection tokens, int start, int end)
    {
        if (start >= end)
        {
            return "";
        }

        var startIndex = tokens[start].Index;
        return line[startIndex..(tokens[end - 1].Index + tokens[end - 1].Length)];
    }

    // "Foo", "Ns.Foo", "a::b": renames and namespace moves, not operator or literal edits.
    private static bool IsQualifiedName(string text)
    {
        var tokens = TokenPattern().Matches(text);
        return tokens.Count > 0 && tokens.All(token => IdentifierPattern().IsMatch(token.Value) || token.Value is "." or ":");
    }

    private static string StripWhitespace(string text) =>
        string.Concat(text.Where(character => !char.IsWhiteSpace(character)));

    private static string Truncate(string text) =>
        text.Length <= MaximumDescriptionLength ? text : text[..MaximumDescriptionLength] + "…";

    private static string ComputePatternId(string signatureKey) =>
        "p" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(signatureKey)))[..11];
}
