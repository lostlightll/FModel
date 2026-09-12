using System.Collections;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Pak.Objects;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.VirtualFileSystem;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FModel.Cli;

// Borrows mounted readers. Disposing this view never disposes or remounts them.
internal sealed class ContainerSnapshot : AbstractFileProvider
{
    public const long MaxFileBytes = 64 * 1024 * 1024;
    public const long MaxReadBytes = 256 * 1024 * 1024;
    public const int MaxPackages = 128;
    private long _readBytes;
    private readonly Dictionary<string, IPackage> _packages = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);
    private AnalysisException? _failure;
    private readonly SnapshotIndex _index;

    public ContainerSnapshot(DefaultFileProvider mounted, IVfsReader selected) : this(mounted, selected, mounted.MountedVfs) { }

    internal ContainerSnapshot(DefaultFileProvider mounted, IVfsReader selected, IEnumerable<IVfsReader> readers) : base(mounted.Versions, mounted.PathComparer)
    {
        MappingsContainer = mounted.MappingsContainer;
        ReadScriptData = false;
        SkipReferencedTextures = true;
        _index = new SnapshotIndex(selected, readers.Where(r => r.ReadOrder <= selected.ReadOrder).ToArray(),
            e => _failure ??= e);
        Files.AddFiles(_index);
    }

    internal static void CheckSize(GameFile file)
    {
        if (file.Size < 0 || file.Size > MaxFileBytes)
            throw new AnalysisException("AssetSizeLimit", "Requested asset or dependency exceeds the 64 MiB per-file limit.");
    }

    private FArchive Read(GameFile file)
    {
        CheckSize(file);
        _readBytes += file.Size;
        if (_readBytes > MaxReadBytes) throw new AnalysisException("ReadBudgetExceeded", "Version view exceeded the 256 MiB read budget.");
        return file.CreateReader();
    }

    public override IPackage LoadPackage(GameFile file)
    {
        try
        {
            if (_packages.TryGetValue(file.Path, out var cached)) return cached;
            if (_packages.Count + _loading.Count >= MaxPackages)
                throw new AnalysisException("DependencyLimit", "Version view exceeded the 128-package dependency limit.");
            if (file is not FPakEntry entry)
                throw new AnalysisException("UnsupportedContainerFormat", "Structured version views currently support pak assets only; compare raw content for other formats.");
            if (!_loading.Add(file.Path)) throw new AnalysisException("DependencyCycle", "Recursive package loading cannot be resolved safely.");
            try
            {
                // Resolve ALL sidecars in the owner's index, including for imported packages.
                entry.Vfs.Files.TryGetValue(file.PathWithoutExtension + ".uexp", out var uexp);
                entry.Vfs.Files.TryGetValue(file.PathWithoutExtension + ".ubulk", out var ubulk);
                entry.Vfs.Files.TryGetValue(file.PathWithoutExtension + ".uptnl", out var uptnl);
                Func<FByteBulkDataHeader?, FArchive?>? bulk = ubulk is null ? null : _ => Read(ubulk);
                Func<FByteBulkDataHeader?, FArchive?>? optional = uptnl is null ? null : _ => Read(uptnl);
                var package = new Package(Read(file), uexp is null ? null : Read(uexp), bulk, optional, this, true);
                _packages.Add(file.Path, package);
                return package;
            }
            finally { _loading.Remove(file.Path); }
        }
        catch (AnalysisException e) { _failure ??= e; throw; }
    }

    public override Task<IPackage> LoadPackageAsync(GameFile file) => Task.FromResult(LoadPackage(file));

    public JToken ReadJson(GameFile file)
    {
        var fatal = CUE4Parse.Globals.FatalObjectSerializationErrors;
        CUE4Parse.Globals.FatalObjectSerializationErrors = true;
        try
        {
            using var text = new LimitedTextWriter();
            using var writer = new JsonTextWriter(text);
            var package = LoadPackage(file);
            if (!package.CanDeserialize) throw new AnalysisException("MissingMappings", "Package cannot be deserialized with the available mappings.");
            JsonSerializer.CreateDefault().Serialize(writer, package.GetExports());
            writer.Flush();
            if (_failure is not null) throw _failure;
            return JToken.Parse(text.ToString(), new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        }
        catch { if (_failure is not null) throw _failure; throw; }
        finally { CUE4Parse.Globals.FatalObjectSerializationErrors = fatal; }
    }

    private sealed class LimitedTextWriter : StringWriter
    {
        private void Check(int count)
        {
            if (GetStringBuilder().Length + (long)count > 32 * 1024 * 1024)
                throw new AnalysisException("SerializationLimit", "Requested asset JSON exceeds the 32 Mi-character in-memory limit.");
        }
        public override void Write(char value) { Check(1); base.Write(value); }
        public override void Write(string? value) { Check(value?.Length ?? 0); base.Write(value); }
        public override void Write(char[] buffer, int index, int count) { Check(count); base.Write(buffer, index, count); }
    }

    // Resolves only requested dependency paths; unrelated same-priority conflicts do not fail a view.
    private sealed class SnapshotIndex(IVfsReader selected, IVfsReader[] eligible, Action<AnalysisException> fail)
        : IReadOnlyDictionary<string, GameFile>
    {
        public bool TryGetValue(string key, out GameFile value)
        {
            if (selected.Files.TryGetValue(key, out value!)) return true;
            IVfsReader? owner = null;
            value = null!;
            foreach (var reader in eligible.OrderByDescending(r => r.ReadOrder))
            {
                if (owner is not null && reader.ReadOrder < owner.ReadOrder) break;
                if (!reader.Files.TryGetValue(key, out var candidate)) continue;
                if (owner is not null)
                {
                    var e = new AnalysisException("AmbiguousDependency", "A requested dependency has multiple sources at the same priority.",
                        eligible.Where(r => r.ReadOrder == owner.ReadOrder && r.Files.ContainsKey(key)).Select(GameSession.Source).ToArray());
                    fail(e); throw e;
                }
                owner = reader;
                value = candidate;
            }
            return owner is not null;
        }
        public GameFile this[string key] => TryGetValue(key, out var file) ? file : throw new KeyNotFoundException();
        public bool ContainsKey(string key) => TryGetValue(key, out _);
        public IEnumerable<string> Keys => eligible.SelectMany(r => r.Files.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
        // AddFiles examines Values for IoStore ids. Borrow entries without resolving unrelated conflicts.
        public IEnumerable<GameFile> Values => eligible.SelectMany(r => r.Files.Values);
        public int Count => eligible.Sum(r => r.FileCount);
        public IEnumerator<KeyValuePair<string, GameFile>> GetEnumerator() => eligible.SelectMany(r => r.Files).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
