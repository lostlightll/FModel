using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.VirtualFileSystem;
using Newtonsoft.Json.Linq;

namespace FModel.Cli;

internal sealed class AnalysisException(string code, string message, object[]? candidates = null) : Exception(message)
{
    public string Code { get; } = code;
    public object[] Candidates { get; } = candidates ?? [];
}

internal sealed partial class GameSession
{
    internal static object Source(IVfsReader reader) => new
    {
        name = reader.Name, path = reader.Path, readOrder = reader.ReadOrder,
        size = new FileInfo(reader.Path).Length, files = reader.FileCount
    };

    private IVfsReader SelectContainer(string selector)
    {
        var fullPath = Path.IsPathFullyQualified(selector);
        if (!fullPath && selector.IndexOfAny(['/', '\\']) >= 0)
            throw new AnalysisException("InvalidContainer", "Use an exact container name or absolute path.");
        var matches = Provider.MountedVfs.Where(r => string.Equals(fullPath ? Path.GetFullPath(r.Path) : r.Name,
            fullPath ? Path.GetFullPath(selector) : selector, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new AnalysisException("ContainerNotFound", "Container is not mounted."),
            _ => throw new AnalysisException("AmbiguousContainer", "Multiple containers match; use an absolute path.", matches.Select(Source).ToArray())
        };
    }

    internal static GameFile ExactFile(IVfsReader reader, string asset)
    {
        // No basename, extension or merged-provider fallback.
        if (reader.Files.TryGetValue(asset.Replace('\\', '/'), out var file)) return file;
        throw new AnalysisException("AssetNotFound", "The exact virtual asset path is absent from the selected container.");
    }

    public object Containers(CliOptions options)
    {
        var query = options.Optional("query") ?? "";
        var offset = options.Number("offset", 0, 0, int.MaxValue);
        var limit = options.Number("limit", 50, 1, 200);
        var matches = Provider.MountedVfs.Where(r => r.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.ReadOrder).ThenBy(r => r.Path, StringComparer.Ordinal).ToArray();
        return new { total = matches.Length, offset, limit, containers = matches.Skip(offset).Take(limit).Select(Source) };
    }

    public object ListContainer(CliOptions options)
    {
        var reader = SelectContainer(options.Required("container"));
        var query = options.Optional("query") ?? "";
        var offset = options.Number("offset", 0, 0, int.MaxValue);
        var limit = options.Number("limit", 50, 1, 200);
        var matches = reader.Files.Values.Where(f => f.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        return new { container = Source(reader), total = matches.Length, offset, limit,
            assets = matches.Skip(offset).Take(limit).Select(f => new { path = f.Path, size = f.Size, source = reader.Path }) };
    }

    private IVfsReader Previous(IVfsReader target, string asset)
    {
        if (Provider.UnloadedVfs.Count != 0)
            throw new AnalysisException("IncompleteMount", "Cannot select a predecessor while some container indices are unavailable.");
        return SelectPrevious(Provider.MountedVfs, target, asset);
    }

    internal static IVfsReader SelectPrevious(IEnumerable<IVfsReader> readers, IVfsReader target, string asset)
    {
        var matches = readers.Where(r => r.ReadOrder < target.ReadOrder && r.Files.ContainsKey(asset))
            .OrderByDescending(r => r.ReadOrder).ToArray();
        if (matches.Length == 0) throw new AnalysisException("NoPreviousVersion", "No lower-priority container contains this asset; specify --against if appropriate.");
        var best = matches.Where(r => r.ReadOrder == matches[0].ReadOrder).ToArray();
        if (best.Length > 1) throw new AnalysisException("AmbiguousPreviousVersion", "Multiple previous containers have the same priority; specify --against.", best.Select(Source).ToArray());
        return best[0];
    }

    public object Diff(CliOptions options)
    {
        var target = SelectContainer(options.Required("container"));
        var afterFile = ExactFile(target, options.Required("asset"));
        var old = options.Optional("against") is { } against ? SelectContainer(against) : Previous(target, afterFile.Path);
        var beforeFile = ExactFile(old, afterFile.Path);
        var maxDifferences = options.Number("max-differences", 100, 1, 1000);
        var maxDepth = options.Number("max-depth", 32, 1, 64);
        var maxNodes = options.Number("max-nodes", 100000, 1, 2000000);
        var bytes = CompareBytes(old, beforeFile, target, afterFile);
        object? fields = null;
        string kind = "binary";
        string? structuredError = null;
        if (afterFile.IsUePackage && beforeFile.IsUePackage)
        {
            if (Provider.UnloadedVfs.Count != 0)
                throw new AnalysisException("IncompleteMount", "Cannot resolve structured version dependencies while container indices are unavailable.");
            try
            {
                using var beforeView = new ContainerSnapshot(Provider, old);
                using var afterView = new ContainerSnapshot(Provider, target);
                var before = beforeView.ReadJson(beforeFile);
                var after = afterView.ReadJson(afterFile);
                fields = BoundedJsonDiff.Compare(before, after, maxDifferences, maxDepth, maxNodes);
                kind = "structured";
            }
            catch (AnalysisException e) when (e.Code is "MissingMappings" or "UnsupportedContainerFormat")
            { structuredError = e.Code; kind = "binary-fallback"; }
            catch (AnalysisException) { throw; }
            catch (Exception e) { structuredError = e.GetType().Name; kind = "binary-fallback"; }
        }
        return new { asset = afterFile.Path, before = Source(old), after = Source(target), kind,
            binary = bytes, structuredError, diff = fields,
            limits = new { maxDifferences, maxDepth, maxNodes, maxAssetBytes = ContainerSnapshot.MaxFileBytes,
                maxDependencyPackages = ContainerSnapshot.MaxPackages, maxReadBytes = ContainerSnapshot.MaxReadBytes } };
    }

    private sealed record RawChange(string path, long? beforeSize, long? afterSize, bool contentChanged);

    private static object CompareBytes(IVfsReader old, GameFile before, IVfsReader target, GameFile after)
    {
        var paths = new List<string> { after.Path };
        if (after.IsUePackage)
            paths.AddRange(new[] { ".uexp", ".ubulk", ".uptnl" }.Select(ext => after.PathWithoutExtension + ext));
        var changes = new List<RawChange>();
        long beforeTotal = 0, afterTotal = 0;
        foreach (var path in paths)
        {
            old.Files.TryGetValue(path, out var left);
            target.Files.TryGetValue(path, out var right);
            if (left is null && right is null) continue;
            if (left is not null) { ContainerSnapshot.CheckSize(left); beforeTotal += left.Size; }
            if (right is not null) { ContainerSnapshot.CheckSize(right); afterTotal += right.Size; }
            var changed = left is null || right is null || left.Size != right.Size || !left.Read().AsSpan().SequenceEqual(right.Read());
            if (changed) changes.Add(new(path, left?.Size, right?.Size, true));
        }
        return new { beforeSize = before.Size, afterSize = after.Size, beforeTotalSize = beforeTotal,
            afterTotalSize = afterTotal, contentChanged = changes.Count > 0, changedFiles = changes };
    }
}
