using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Pak.Objects;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using FModel.Cli;

public sealed class ContainerIsolationTests
{
    private const string Main = "Game/Content/Main.uasset";
    private const string Dependency = "Game/Content/Dependency.uasset";

    [Fact]
    public void OldViewCannotResolveHigherPriorityDependencies()
    {
        var old = new FakeReader("old.pak", 100, Main);
        var newer = new FakeReader("new.pak", 200, Dependency);
        using var provider = Provider();
        using var snapshot = new ContainerSnapshot(provider, old, [old, newer]);
        Assert.False(snapshot.Files.TryGetValue(Dependency, out _));
        Assert.Empty(old.Reads);
        Assert.Empty(newer.Reads);
    }

    [Fact]
    public void DependencyUsesHighestEligibleSourceRegardlessOfEnumerationOrder()
    {
        var target = new FakeReader("target.pak", 300, Main);
        var low = new FakeReader("low.pak", 100, Dependency);
        var middle = new FakeReader("middle.pak", 200, Dependency);
        var future = new FakeReader("future.pak", 400, Dependency);
        using var provider = Provider();
        using var snapshot = new ContainerSnapshot(provider, target, [future, low, target, middle]);
        Assert.Same(middle.Files[Dependency], snapshot.Files[Dependency]);
    }

    [Fact]
    public void ExplicitSelectedContainerWinsItsOwnEntriesOverSamePriorityPeers()
    {
        var selected = new FakeReader("selected.pak", 200, Main);
        var peer = new FakeReader("peer.pak", 200, Main);
        using var provider = Provider();
        using var snapshot = new ContainerSnapshot(provider, selected, [peer, selected]);
        Assert.Same(selected.Files[Main], snapshot.Files[Main]);
    }

    [Fact]
    public void RequestedAmbiguousDependencyFailsWithBothCandidates()
    {
        var selected = new FakeReader("selected.pak", 300, Main);
        var left = new FakeReader("left.pak", 200, Dependency);
        var right = new FakeReader("right.pak", 200, Dependency);
        using var provider = Provider();
        using var snapshot = new ContainerSnapshot(provider, selected, [selected, left, right]);
        Assert.Same(selected.Files[Main], snapshot.Files[Main]);
        var error = Assert.Throws<AnalysisException>(() => snapshot.Files[Dependency]);
        Assert.Equal("AmbiguousDependency", error.Code);
        Assert.Equal(2, error.Candidates.Length);
    }

    [Fact]
    public void PackageAndExportSidecarAreReadFromTheSameOwner()
    {
        var sidecar = Main.Replace(".uasset", ".uexp");
        var selected = new FakeReader("selected.pak", 200, Main, sidecar);
        var older = new FakeReader("older.pak", 100, Main, sidecar);
        using var provider = Provider();
        using var snapshot = new ContainerSnapshot(provider, selected, [selected, older]);
        // Invalid package bytes stop parsing after both archives have been requested.
        Assert.NotNull(Record.Exception(() => snapshot.LoadPackage(selected.Files[Main])));
        Assert.Equal(new[] { Main, sidecar }, selected.Reads);
        Assert.Empty(older.Reads);
    }

    [Fact]
    public void MissingOwnerSidecarNeverFallsBackToAnotherContainer()
    {
        var sidecar = Main.Replace(".uasset", ".uexp");
        var selected = new FakeReader("selected.pak", 200, Main);
        var older = new FakeReader("older.pak", 100, sidecar);
        using var provider = Provider();
        using var snapshot = new ContainerSnapshot(provider, selected, [selected, older]);
        Assert.NotNull(Record.Exception(() => snapshot.LoadPackage(selected.Files[Main])));
        Assert.Equal(new[] { Main }, selected.Reads);
        Assert.Empty(older.Reads);
    }

    [Fact]
    public void DependencySidecarAlsoStaysWithTheDependencyOwner()
    {
        var sidecar = Dependency.Replace(".uasset", ".uexp");
        var selected = new FakeReader("selected.pak", 300, Main, sidecar);
        var owner = new FakeReader("owner.pak", 200, Dependency, sidecar);
        using var provider = Provider();
        using var snapshot = new ContainerSnapshot(provider, selected, [selected, owner]);
        Assert.NotNull(Record.Exception(() => snapshot.LoadPackage(snapshot.Files[Dependency])));
        Assert.Equal(new[] { Dependency, sidecar }, owner.Reads);
        Assert.Empty(selected.Reads);
    }

    [Fact]
    public void DisposingSnapshotDoesNotDisposeBorrowedReaders()
    {
        var selected = new FakeReader("selected.pak", 100, Main);
        using var provider = Provider();
        using (var snapshot = new ContainerSnapshot(provider, selected, [selected]))
            Assert.Same(selected.Files[Main], snapshot.Files[Main]);
        Assert.False(selected.Disposed);
    }

    [Fact]
    public void PreviousSelectsNearestStrictlyLowerPriority()
    {
        var target = new FakeReader("target.pak", 300, Main);
        var low = new FakeReader("low.pak", 100, Main);
        var previous = new FakeReader("previous.pak", 200, Main);
        var peer = new FakeReader("peer.pak", 300, Main);
        var future = new FakeReader("future.pak", 400, Main);
        Assert.Same(previous, GameSession.SelectPrevious([low, future, peer, previous, target], target, Main));
    }

    [Fact]
    public void PreviousTieRejectsWithCandidatesInsteadOfChoosingEnumerationOrder()
    {
        var target = new FakeReader("target.pak", 300, Main);
        var left = new FakeReader("left.pak", 200, Main);
        var right = new FakeReader("right.pak", 200, Main);
        var error = Assert.Throws<AnalysisException>(() => GameSession.SelectPrevious([target, left, right], target, Main));
        Assert.Equal("AmbiguousPreviousVersion", error.Code);
        Assert.Equal(2, error.Candidates.Length);
    }

    private static DefaultFileProvider Provider() => new(System.IO.Path.GetTempPath(), SearchOption.TopDirectoryOnly,
        new VersionContainer(EGame.GAME_UE4_27), StringComparer.OrdinalIgnoreCase);

    private sealed class FakeReader : IVfsReader
    {
        public FakeReader(string name, long priority, params string[] paths)
        {
            Name = name;
            ReadOrder = priority;
            Files = paths.ToDictionary(p => p, p => (GameFile)new FPakEntry(this, p), StringComparer.OrdinalIgnoreCase);
        }
        // Source metadata can inspect a real existing file without writing test pak fixtures.
        public string Path => typeof(ContainerIsolationTests).Assembly.Location;
        public string Name { get; }
        public long ReadOrder { get; }
        public IReadOnlyDictionary<string, GameFile> Files { get; }
        public int FileCount => Files.Count;
        public string MountPoint => "";
        public bool HasDirectoryIndex => true;
        public bool IsConcurrent { get; set; }
        public VersionContainer Versions { get; set; } = new(EGame.GAME_UE4_27);
        public EGame Game { get => Versions.Game; set => Versions.Game = value; }
        public FPackageFileVersion Ver { get => Versions.Ver; set => Versions.Ver = value; }
        public List<string> Reads { get; } = [];
        public bool Disposed { get; private set; }
        public void Mount(StringComparer pathComparer) => throw new InvalidOperationException("Tests must borrow existing indices.");
        public void MountTo(FileProviderDictionary files, StringComparer pathComparer, EventHandler<int>? vfsMounted = null)
            => throw new InvalidOperationException("Tests must borrow existing indices.");
        public byte[] Extract(VfsEntry entry, FByteBulkDataHeader? header = null)
        {
            Assert.Same(this, entry.Vfs);
            Reads.Add(entry.Path);
            return [0, 0, 0, 0];
        }
        public void Dispose() => Disposed = true;
    }
}
