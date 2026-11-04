using Sift.Core.Diff;

namespace Sift.Core.Analysis;

public sealed record RuleResult(string Bucket, string Reason);

/// <summary>High-confidence classifications that need no AI. The AI can override any of them.</summary>
public static class RuleClassifier
{
    private static readonly HashSet<string> LockFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "packages.lock.json", "npm-shrinkwrap.json",
        "Gemfile.lock", "Pipfile.lock", "poetry.lock", "uv.lock", "composer.lock", "Cargo.lock", "go.sum",
        "pubspec.lock", "flake.lock", "Package.resolved", "gradle.lockfile", ".terraform.lock.hcl", "bun.lockb",
    };

    private static readonly string[] GeneratedSuffixes =
    [
        ".min.js", ".min.css", ".map", ".Designer.cs", ".designer.cs", ".g.cs", ".g.i.cs", ".generated.cs",
        ".generated.ts", ".pb.go", "_pb2.py", ".snap",
    ];

    private static readonly string[] GeneratedDirectories = ["dist", "bin", "obj", "node_modules", "wwwroot/lib", "vendor", "__snapshots__"];

    private static readonly string[] DocumentationExtensions = [".md", ".mdx", ".rst", ".adoc"];

    private static readonly string[] TestDirectoryNames = ["test", "tests", "__tests__", "spec", "specs"];

    private static readonly string[] TestFileSuffixes =
    [
        "Tests.cs", "Test.cs", "Spec.cs", ".spec.ts", ".spec.js", ".test.ts", ".test.js", ".test.tsx", ".spec.tsx",
        "_test.go", "_test.py", "Test.java", "Tests.java",
    ];

    public static RuleResult? ClassifyFile(DiffFile file)
    {
        var path = file.Path;
        var fileName = path[(path.LastIndexOf('/') + 1)..];
        var segments = path.Split('/');

        if (LockFileNames.Contains(fileName))
        {
            return new RuleResult("noise", "Lockfile");
        }

        if (GeneratedSuffixes.Any(suffix => fileName.EndsWith(suffix, StringComparison.Ordinal)) ||
            fileName.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal) ||
            GeneratedDirectories.Any(directory => ContainsDirectory(path, directory)))
        {
            return new RuleResult("noise", "Generated file");
        }

        if (file.IsBinary)
        {
            return new RuleResult("noise", "Binary file");
        }

        if (file.Hunks.Count == 0)
        {
            return file.ChangeKind switch
            {
                FileChangeKind.Renamed => new RuleResult("refactor", "Moved without content changes"),
                FileChangeKind.Copied => new RuleResult("refactor", "Copied without content changes"),
                _ when file.OldMode != file.NewMode => new RuleResult("noise", "File mode change only"),
                _ when file.LineEndingChangedLines > 0 => new RuleResult("noise", "Line endings only"),
                _ => null,
            };
        }

        if (DocumentationExtensions.Any(extension => fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) ||
            segments.SkipLast(1).Any(segment => segment.Equals("docs", StringComparison.OrdinalIgnoreCase)))
        {
            return new RuleResult("docs", "Documentation");
        }

        if (TestFileSuffixes.Any(suffix => fileName.EndsWith(suffix, StringComparison.Ordinal)) ||
            fileName.StartsWith("test_", StringComparison.Ordinal) ||
            segments.SkipLast(1).Any(IsTestDirectory))
        {
            return new RuleResult("tests", "Test code");
        }

        return null;
    }

    public static RuleResult? ClassifyHunk(DiffHunk hunk, MoveMap moves)
    {
        if (PatternDetector.IsWhitespaceOnly(hunk))
        {
            return new RuleResult("noise", "Whitespace only");
        }

        return moves.IsFullyMoved(hunk) ? new RuleResult("refactor", "Moved code, unchanged") : null;
    }

    public static RuleResult? ClassifyPattern(PatternGroup pattern) =>
        pattern is { Kind: PatternKind.Replacement, IsIdentifierReplacement: true }
            ? new RuleResult("refactor", "Repeated rename")
            : null;

    private static bool IsTestDirectory(string segment) =>
        TestDirectoryNames.Contains(segment, StringComparer.OrdinalIgnoreCase) ||
        segment.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
        segment.EndsWith(".UnitTests", StringComparison.OrdinalIgnoreCase) ||
        segment.EndsWith(".IntegrationTests", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsDirectory(string path, string directory) =>
        path.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/" + directory + "/", StringComparison.OrdinalIgnoreCase);
}
