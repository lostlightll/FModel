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
            "containers" => ["profile", "query", "offset", "limit"],
            "list" => ["profile", "container", "query", "offset", "limit"],
            "diff" => ["profile", "container", "asset", "against", "max-differences", "max-depth", "max-nodes"],
            "lua-functions" => ["profile", "container", "asset", "dialect", "query", "offset", "limit"],
            "lua-disasm" => ["profile", "container", "asset", "dialect", "function", "offset", "limit"],
            "lua-diff" => ["profile", "container", "asset", "dialect", "against", "function", "offset", "limit", "max-changes", "max-work"],
            "inspect" or "extract" => ["profile", "asset", "output-directory"],
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
        if (args[0] is "inspect" or "extract" or "diff") result.Required("asset");
        if (args[0] is "list" or "diff") result.Required("container");
        if (args[0].StartsWith("lua-"))
        {
            result.Required("asset"); result.Required("container");
            if (result.Optional("dialect") is { } dialect && dialect is not ("auto" or "lua53" or "slua53"))
                throw new ArgumentException("--dialect must be auto, lua53 or slua53.");
        }
        if (args[0] == "lua-disasm") result.Required("function");
        result.Number("offset", 0, 0, int.MaxValue);
        result.Number("limit", 50, 1, 200);
        result.Number("max-differences", 100, 1, 1000);
        result.Number("max-depth", 32, 1, 64);
        result.Number("max-nodes", 100000, 1, 2000000);
        result.Number("max-changes", 100, 1, 1000);
        result.Number("max-work", 2000000, 1, 10000000);
        return result;
    }
}
