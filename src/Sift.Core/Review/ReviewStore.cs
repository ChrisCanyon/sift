using System.Text.Json;

namespace Sift.Core.Review;

public sealed record ReviewListing(
    string Path,
    string? Title,
    string RepoName,
    int? PrNumber,
    string? PrUrl,
    string HeadSha,
    DateTimeOffset Modified,
    string? Error,
    string? BaseRef = null,
    string? HeadRef = null);

/// <summary>Saved reviews under ~/.sift/reviews, plus any file Sift was launched with.</summary>
public sealed class ReviewStore(string rootPath)
{
    private readonly HashSet<string> registeredPaths = new(StringComparer.OrdinalIgnoreCase);

    public string RootPath { get; } = Path.GetFullPath(rootPath);

    public void Register(string reviewPath) => registeredPaths.Add(Path.GetFullPath(reviewPath));

    /// <summary>Only saved or explicitly registered review files can be opened.</summary>
    public bool IsKnown(string reviewPath)
    {
        var fullPath = Path.GetFullPath(reviewPath);
        return registeredPaths.Contains(fullPath) || IsUnderRoot(fullPath) && IsReviewFile(fullPath);
    }

    public IReadOnlyList<ReviewListing> List(int maximumCount)
    {
        var savedPaths = Directory.Exists(RootPath)
            ? Directory.EnumerateFiles(RootPath, "*.json", SearchOption.AllDirectories).Where(IsReviewFile)
            : [];

        return savedPaths
            .Concat(registeredPaths.Where(File.Exists))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (Path: path, Modified: File.GetLastWriteTimeUtc(path)))
            .OrderByDescending(entry => entry.Modified)
            .Take(maximumCount)
            .Select(entry => ReadListing(entry.Path, entry.Modified))
            .ToList();
    }

    public async Task<ReviewDocument> LoadAsync(string reviewPath, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(reviewPath);
        var document = await JsonSerializer.DeserializeAsync<ReviewDocument>(stream, SiftJson.Options, cancellationToken)
            ?? throw new InvalidDataException("Review file is empty.");

        if (document.Source is null || string.IsNullOrWhiteSpace(document.Source.RepoPath) || string.IsNullOrWhiteSpace(document.Source.HeadSha) || string.IsNullOrWhiteSpace(document.Source.BaseSha))
        {
            throw new InvalidDataException("Review file needs source.repoPath, source.baseSha and source.headSha.");
        }

        return document;
    }

    private static ReviewListing ReadListing(string path, DateTime modifiedUtc)
    {
        var modified = new DateTimeOffset(modifiedUtc, TimeSpan.Zero);
        try
        {
            using var stream = File.OpenRead(path);
            var document = JsonSerializer.Deserialize<ReviewDocument>(stream, SiftJson.Options);
            var source = document?.Source;
            return new ReviewListing(
                path,
                document?.Title,
                source is null ? "" : Path.GetFileName(source.RepoPath.TrimEnd('/', '\\')),
                source?.PrNumber,
                source?.PrUrl,
                source?.HeadSha ?? "",
                modified,
                null,
                source?.BaseRef,
                source?.HeadRef);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return new ReviewListing(path, null, "", null, null, "", modified, exception.Message);
        }
    }

    private bool IsUnderRoot(string fullPath) =>
        fullPath.StartsWith(RootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsReviewFile(string path) =>
        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(Sift.Core.State.ReviewStateStore.StateFileSuffix, StringComparison.OrdinalIgnoreCase);
}
