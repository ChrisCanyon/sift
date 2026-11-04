using System.Text.Json;
using Sift.Core;
using Sift.Core.Diff;
using Sift.Core.Git;

namespace Sift;

public static class HunksCommand
{
    public const int IncompleteDiffExitCode = 3;

    public static async Task<int> RunAsync(string[] commandArguments)
    {
        var options = CommandLineOptions.Parse(commandArguments);
        if (!options.TryGetValue("base", out var baseReference))
        {
            Console.Error.WriteLine("Usage: sift hunks --base <ref> [--head <ref>] [--repo <path>] [--preview-lines <n>] [--direct] [--full] [--out <file>]");
            return 2;
        }

        var repositoryPath = Path.GetFullPath(options.GetValueOrDefault("repo", Directory.GetCurrentDirectory()));
        if (!Directory.Exists(repositoryPath))
        {
            Console.Error.WriteLine($"Repository path not found: {repositoryPath}");
            return 2;
        }

        var headReference = options.GetValueOrDefault("head", "HEAD");
        var previewLineCount = int.Parse(options.GetValueOrDefault("preview-lines", "4"));

        try
        {
            var diffBase = options.ContainsKey("direct") ? DiffBase.Direct : DiffBase.MergeBase;
            var diff = await new GitRepository(repositoryPath).GetDiffAsync(baseReference, headReference, diffBase);
            var hunkIndexJson = JsonSerializer.Serialize(HunkIndex.FromDiff(diff, previewLineCount, compact: !options.ContainsKey("full")), SiftJson.FileOptions);
            if (options.TryGetValue("out", out var outputPath))
            {
                await File.WriteAllTextAsync(outputPath, hunkIndexJson);
            }
            else
            {
                Console.Out.Write(hunkIndexJson);
            }

            if (diff.Reconciliation is { IsComplete: false } reconciliation)
            {
                Console.Error.WriteLine($"Diff does not reconcile with git ({reconciliation.Problems.Count} problems); the listing is incomplete:");
                foreach (var problem in reconciliation.Problems.Take(20))
                {
                    Console.Error.WriteLine($"  {problem.Kind}: {problem.Path}: {problem.Detail}");
                }

                return IncompleteDiffExitCode;
            }

            return 0;
        }
        catch (GitCommandException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
