using FModel.Cli;
using Newtonsoft.Json;
using Serilog;

var stdout = Console.Out;
// CUE4Parse and native helpers must not contaminate machine-readable stdout.
Console.SetOut(TextWriter.Null);
// Third-party diagnostics may embed AES/configuration values. Only emit our safe errors.
Log.Logger = new LoggerConfiguration().CreateLogger();
var stage = "arguments";
try
{
    var options = CliOptions.Parse(args);
    if (options.Command == "help")
    {
        stdout.WriteLine("Lua bytecode analysis (data only, never executes scripts):\nlua-functions: --profile P --container C --asset A [--query text] [--offset 0] [--limit 20]\nlua-disasm: --profile P --container C --asset A --function <id or $> [--offset 0] [--limit 20]\nlua-diff: --profile P --container C --asset A [--against C] [--function id] [--offset 0] [--limit 20] [--max-changes 100 (1..1000)] [--max-work 2000000 (1..10000000)]\nAll Lua commands: [--dialect auto|lua53|slua53]. Functions/changes/instructions are paged; limit 1..200. Ambiguous identities fail; --function $ explicitly scopes analysis to the root. These commands disassemble bytecode, not complete Lua source. Opcode sources and MIT license: Lua/OPCODE-SOURCES.md (also embedded in EXE).\n");
        stdout.WriteLine("FModel.Cli <mount|search|containers|list|diff|inspect|extract> --profile <JSON path>\ncontainers/list/search: [--query text] [--offset 0] [--limit 50 (max 200)]\nlist: --container <name or absolute path>\ndiff: --container <target> --asset <exact virtual path> [--against <old container>] [--max-differences 100 (max 1000)] [--max-depth 32 (max 64)] [--max-nodes 100000 (max 2000000)]\nsearch: [--extension uasset]\ninspect/extract: --asset <virtual path> [--output-directory <path>]\nJSON stdout; exits: 0 success, 1 failure, 2 incomplete mount. Read commands create no exports. Diff before=old, after=target; automatic predecessor uses strictly lower ReadOrder, ties are errors. inspect/extract never overwrite existing files.");
        return 0;
    }
    stage = "profile";
    var profilePath = Path.GetFullPath(options.Required("profile"));
    var profile = JsonConvert.DeserializeObject<GameProfile>(File.ReadAllText(profilePath),
        new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error }) ?? throw new ArgumentException("Empty profile.");
    var profileDirectory = Path.GetDirectoryName(profilePath)!;
    profile.Directory = ProfilePaths.Resolve(profile.Directory, profileDirectory);
    var export = options.Command is "inspect" or "extract";
    if (export) profile.OutputDirectory = options.Optional("output-directory") is { } output
        ? Path.GetFullPath(output) : ProfilePaths.Resolve(profile.OutputDirectory, profileDirectory);
    if (profile.Mappings is not null) profile.Mappings = ProfilePaths.Resolve(profile.Mappings, profileDirectory);
    stage = "mount";
    using var session = new GameSession(profile, export);
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
        "containers" => session.Containers(options),
        "list" => session.ListContainer(options),
        "diff" => session.Diff(options),
        "lua-functions" or "lua-disasm" => session.LuaRead(options),
        "lua-diff" => session.LuaDiff(options),
        "inspect" => session.Inspect(options.Required("asset")),
        "extract" => session.Extract(options.Required("asset")),
        _ => throw new ArgumentException("Unknown command.")
    };
    stdout.WriteLine(JsonConvert.SerializeObject(new { ok = true, mountComplete = complete, result }));
    return complete ? 0 : 2;
}
catch (AnalysisException exception)
{
    stdout.WriteLine(JsonConvert.SerializeObject(new { ok = false, stage, error = exception.Code, message = exception.Message, candidates = exception.Candidates }));
    return 1;
}
catch (Exception exception)
{
    // Third-party exceptions can include configuration values; expose only the type.
    var message = stage == "arguments" ? exception.Message : "Operation failed. Check paths, AES configuration, mappings and local compression dependencies.";
    stdout.WriteLine(JsonConvert.SerializeObject(new { ok = false, stage, error = exception.GetType().Name, message }));
    return 1;
}
finally { Log.CloseAndFlush(); }
