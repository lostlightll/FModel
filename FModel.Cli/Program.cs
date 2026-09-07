using FModel.Cli;
using Newtonsoft.Json;
using Serilog;

var stdout = Console.Out;
// CUE4Parse and native helpers must not contaminate machine-readable stdout.
Console.SetOut(Console.Error);
Log.Logger = new LoggerConfiguration().MinimumLevel.Warning().WriteTo.Console(standardErrorFromLevel: Serilog.Events.LogEventLevel.Verbose).CreateLogger();
var stage = "arguments";
try
{
    var options = CliOptions.Parse(args);
    if (options.Command == "help")
    {
        stdout.WriteLine("FModel.Cli <mount|search|inspect|extract> --profile <absolute JSON path>\nsearch: [--query text] [--extension uasset] [--offset 0] [--limit 50 (max 200)]\ninspect/extract: --asset <virtual path from search>\nResults: JSON on stdout; diagnostics: stderr; exits: 0 success, 1 failure, 2 incomplete mount.\ninspect writes package exports as JSON; extract writes original package and sidecars. Existing outputs are never overwritten.");
        return 0;
    }
    stage = "profile";
    var profilePath = Path.GetFullPath(options.Required("profile"));
    var profile = JsonConvert.DeserializeObject<GameProfile>(File.ReadAllText(profilePath),
        new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error }) ?? throw new ArgumentException("Empty profile.");
    var profileDirectory = Path.GetDirectoryName(profilePath)!;
    profile.Directory = ProfilePaths.Resolve(profile.Directory, profileDirectory);
    profile.OutputDirectory = ProfilePaths.Resolve(profile.OutputDirectory, profileDirectory);
    if (profile.Mappings is not null) profile.Mappings = ProfilePaths.Resolve(profile.Mappings, profileDirectory);
    stage = "mount";
    using var session = new GameSession(profile);
    var complete = session.Provider.MountedVfs.Count > 0 && session.Provider.UnloadedVfs.Count == 0;
    if (options.Command == "mount" || session.Provider.MountedVfs.Count == 0)
    {
        stdout.WriteLine(JsonConvert.SerializeObject(new { ok = complete, result = session.MountSummary() }));
        return complete ? 0 : 2;
    }
    stage = options.Command;
    var result = options.Command switch
    {
        "search" => session.Search(options),
        "inspect" => session.Inspect(options.Required("asset")),
        "extract" => session.Extract(options.Required("asset")),
        _ => throw new ArgumentException("Unknown command.")
    };
    stdout.WriteLine(JsonConvert.SerializeObject(new { ok = true, mountComplete = complete, result }));
    return complete ? 0 : 2;
}
catch (Exception exception)
{
    // Third-party exceptions can include configuration values; expose only the type.
    var message = stage == "arguments" ? exception.Message : "Operation failed. Check paths, AES configuration, mappings and local compression dependencies.";
    stdout.WriteLine(JsonConvert.SerializeObject(new { ok = false, stage, error = exception.GetType().Name, message }));
    return 1;
}
finally { Log.CloseAndFlush(); }
