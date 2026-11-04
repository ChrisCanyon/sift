namespace Sift;

public static class CommandLineOptions
{
    /// <summary>"--name value" pairs; flags without a value (e.g. --no-open) map to an empty string.</summary>
    public static Dictionary<string, string> Parse(string[] commandArguments)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < commandArguments.Length; index++)
        {
            var name = commandArguments[index].TrimStart('-');
            var hasValue = index + 1 < commandArguments.Length && !commandArguments[index + 1].StartsWith("--", StringComparison.Ordinal);
            options[name] = hasValue ? commandArguments[++index] : "";
        }

        return options;
    }
}
