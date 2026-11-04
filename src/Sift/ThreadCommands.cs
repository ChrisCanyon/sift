using System.Net.Http.Json;
using System.Text.Json;
using Sift.Core;
using Sift.Core.State;

namespace Sift;

/// <summary>CLI the AI uses to answer threads raised in the UI.</summary>
public static class ThreadCommands
{
    private const string ReplyUsage = "Usage: sift reply --review <file> --thread <id> (--body <text> | --body-file <file>) [--status answered|done|pushed]";

    public static async Task<int> ReplyAsync(string[] commandArguments)
    {
        var options = CommandLineOptions.Parse(commandArguments);
        if (!options.TryGetValue("review", out var reviewArgument) || !options.TryGetValue("thread", out var threadId))
        {
            Console.Error.WriteLine(ReplyUsage);
            return 2;
        }

        var body = options.TryGetValue("body-file", out var bodyFile)
            ? await File.ReadAllTextAsync(bodyFile)
            : options.GetValueOrDefault("body");
        if (string.IsNullOrWhiteSpace(body))
        {
            Console.Error.WriteLine(ReplyUsage);
            return 2;
        }

        ThreadStatus? status = null;
        if (options.TryGetValue("status", out var statusArgument))
        {
            if (!Enum.TryParse<ThreadStatus>(statusArgument, ignoreCase: true, out var parsedStatus))
            {
                Console.Error.WriteLine($"Unknown status '{statusArgument}'. {ReplyUsage}");
                return 2;
            }

            status = parsedStatus;
        }

        var reviewPath = Path.GetFullPath(reviewArgument);
        var request = new MessageRequest(reviewPath, "claude", body.Trim(), status);

        var serverInfo = ReadServerInfo();
        if (serverInfo is not null)
        {
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(serverInfo.Url), Timeout = TimeSpan.FromSeconds(10) };
                client.DefaultRequestHeaders.Add(ServerCommand.RequestHeaderName, "1");
                using var response = await client.PostAsJsonAsync($"/api/threads/{Uri.EscapeDataString(threadId)}/messages", request, SiftJson.Options);
                if (response.IsSuccessStatusCode)
                {
                    Console.Out.WriteLine("Reply delivered to the open Sift page.");
                    return 0;
                }

                Console.Error.WriteLine($"Sift server rejected the reply: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
                return 1;
            }
            catch (HttpRequestException)
            {
                // Stale server.json from a crashed server; fall through to writing state directly.
            }
        }

        try
        {
            await new ReviewStateStore().AddMessageAsync(reviewPath, threadId, "claude", body.Trim(), status);
            Console.Out.WriteLine("Sift is not running; reply saved and will show when the review is opened.");
            return 0;
        }
        catch (ThreadNotFoundException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    public static async Task<int> ListAsync(string[] commandArguments)
    {
        var options = CommandLineOptions.Parse(commandArguments);
        if (!options.TryGetValue("review", out var reviewArgument))
        {
            Console.Error.WriteLine("Usage: sift threads --review <file> [--open]");
            return 2;
        }

        var state = await new ReviewStateStore().LoadAsync(Path.GetFullPath(reviewArgument));
        var threads = options.ContainsKey("open")
            ? state.Threads.Where(thread => thread.Status == ThreadStatus.Open).ToList()
            : state.Threads;
        Console.Out.WriteLine(JsonSerializer.Serialize(threads, SiftJson.IndentedOptions));
        return 0;
    }

    private static ServerInfo? ReadServerInfo()
    {
        try
        {
            return JsonSerializer.Deserialize<ServerInfo>(File.ReadAllText(SiftPaths.ServerInfoPath), SiftJson.Options);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }
}
