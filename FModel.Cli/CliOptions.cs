namespace FModel.Cli;

public sealed record CliOptions(string Command, IReadOnlyDictionary<string, string> Values)
{
    public string Required(string name) => Values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value : throw new ArgumentException($"Missing --{name}.");

    public string? Optional(string name) => Values.GetValueOrDefault(name);

    public int Number(string name, int fallback, int min, int max)
    {
        if (!Values.TryGetValue(name, out var text)) return fallback;
        return int.TryParse(text, out var number) && number >= min && number <= max
            ? number : throw new ArgumentException($"--{name} must be between {min} and {max}.");
    }

    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0 || args is ["--help"] or ["help"]) return new("help", new Dictionary<string, string>());
        var allowed = args[0] switch
        {
            "mount" => new[] { "profile" },
            "search" => ["profile", "query", "extension", "offset", "limit"],
            "inspect" or "extract" => ["profile", "asset"],
            _ => throw new ArgumentException("Unknown command. Use --help.")
        };
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--") || !allowed.Contains(args[i][2..]) || i + 1 == args.Length || args[i + 1].StartsWith("--"))
                throw new ArgumentException("Invalid option or missing value. Use --help.");
            if (!values.TryAdd(args[i][2..], args[i + 1])) throw new ArgumentException("Duplicate option.");
        }
        var result = new CliOptions(args[0], values);
        result.Required("profile");
        if (args[0] is "inspect" or "extract") result.Required("asset");
        result.Number("offset", 0, 0, int.MaxValue);
        result.Number("limit", 50, 1, 200);
        return result;
    }
}
