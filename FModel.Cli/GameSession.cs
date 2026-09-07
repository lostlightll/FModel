using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;

namespace FModel.Cli;

public sealed class GameProfile
{
    public string Game { get; init; } = "GAME_AssaultFireFuture";
    public string Directory { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public string? AesKey { get; init; }
    public string AesKeyEnvironmentVariable { get; init; } = "FMODEL_AES_KEY";
    public string? Mappings { get; set; }
}

internal sealed class GameSession : IDisposable
{
    public DefaultFileProvider Provider { get; }
    public string OutputRoot { get; }

    public GameSession(GameProfile profile)
    {
        if (!Enum.TryParse<EGame>(profile.Game, out var game) || !Enum.IsDefined(game))
            throw new ArgumentException("Unknown game enum.");
        if (!Directory.Exists(profile.Directory)) throw new DirectoryNotFoundException("Game directory not found.");
        if (!Path.IsPathFullyQualified(profile.Directory) || !Path.IsPathFullyQualified(profile.OutputDirectory))
            throw new ArgumentException("Game and output directories must be absolute paths.");
        OutputRoot = Path.GetFullPath(profile.OutputDirectory);
        if (OutputFiles.IsWithin(OutputRoot, profile.Directory) || OutputFiles.IsWithin(profile.Directory, OutputRoot))
            throw new ArgumentException("Game and output directories must not overlap.");
        OutputFiles.RejectLinks(OutputRoot);
        var key = Environment.GetEnvironmentVariable(profile.AesKeyEnvironmentVariable) ?? profile.AesKey;
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Set the profile AES environment variable or local AesKey.");
        FAesKey aes;
        try { aes = new FAesKey(key); }
        catch { throw new ArgumentException("Invalid AES key; expected 32 bytes in hexadecimal."); }
        Provider = new DefaultFileProvider(profile.Directory, SearchOption.AllDirectories, new VersionContainer(game), StringComparer.OrdinalIgnoreCase);
        try
        {
            if (profile.Mappings is not null)
            {
                if (!Path.IsPathFullyQualified(profile.Mappings) || !File.Exists(profile.Mappings))
                    throw new FileNotFoundException("Mappings must name an existing absolute .usmap path.");
                Provider.MappingsContainer = new FileUsmapTypeMappingsProvider(profile.Mappings);
            }
            Provider.Initialize();
            Provider.SubmitKey(new FGuid(), aes);
            Provider.PostMount();
        }
        catch { Provider.Dispose(); throw; }
    }

    public object MountSummary() => new
    {
        game = Provider.Versions.Game.ToString(),
        mounted = Provider.MountedVfs.Count,
        unloaded = Provider.UnloadedVfs.Count,
        files = Provider.Files.Count,
        missingKeyGuids = Provider.RequiredKeys.Select(k => k.ToString()).ToArray(),
        outputDirectory = OutputRoot
    };

    public object Search(CliOptions options)
    {
        var query = options.Optional("query") ?? "";
        var extension = options.Optional("extension")?.TrimStart('.');
        var offset = options.Number("offset", 0, 0, int.MaxValue);
        var limit = options.Number("limit", 50, 1, 200);
        var matches = Provider.Files.Values.Where(f => f.Path.Contains(query, StringComparison.OrdinalIgnoreCase) &&
            (extension is null || f.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        return new { total = matches.Length, offset, limit, assets = matches.Skip(offset).Take(limit).Select(f => new { path = f.Path, size = f.Size }) };
    }

    public object Inspect(string asset)
    {
        var file = Provider[asset];
        var path = OutputFiles.Resolve(OutputRoot, "json/" + file.Path + ".json");
        if (File.Exists(path)) throw new IOException("Output already exists.");
        var exports = Provider.LoadPackage(file).GetExports().ToArray();
        OutputFiles.Write(path, stream =>
        {
            using var writer = new StreamWriter(stream, leaveOpen: true);
            using var json = new JsonTextWriter(writer) { Formatting = Formatting.Indented };
            JsonSerializer.CreateDefault().Serialize(json, exports);
        });
        return new { asset = file.Path, exports = exports.Length, output = path, bytes = new FileInfo(path).Length };
    }

    public object Extract(string asset)
    {
        var file = Provider[asset];
        var data = Provider.SavePackage(file);
        var paths = data.Keys.ToDictionary(p => p, p => OutputFiles.Resolve(OutputRoot, "raw/" + p));
        if (paths.Values.Any(File.Exists)) throw new IOException("Output already exists; no files were written.");
        var written = new List<string>();
        try
        {
            foreach (var (name, bytes) in data)
            {
                OutputFiles.Write(paths[name], stream => stream.Write(bytes));
                written.Add(paths[name]);
            }
        }
        catch
        {
            foreach (var path in written) File.Delete(path);
            throw;
        }
        return new { asset = file.Path, files = paths.Select(p => new { output = p.Value, bytes = data[p.Key].LongLength }) };
    }

    public void Dispose() => Provider.Dispose();
}
