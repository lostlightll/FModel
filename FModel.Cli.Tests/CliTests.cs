using FModel.Cli;

public sealed class CliTests
{
    [Fact]
    public void ReadOnlySessionAcceptsMissingOutputDirectory()
    {
        var profile = new GameProfile
        {
            Directory = Path.GetDirectoryName(typeof(CliTests).Assembly.Location)!,
            AesKey = new string('0', 64),
            AesKeyEnvironmentVariable = "FMODEL_TEST_UNUSED_" + Guid.NewGuid().ToString("N")
        };
        using var session = new GameSession(profile);
        Assert.Equal("", session.OutputRoot);
        Assert.Empty(session.Provider.MountedVfs);
    }

    [Fact]
    public void RelativeProfilePathsUseProfileDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "fmodel-profile");
        Assert.Equal(Path.Combine(root, "exports"), ProfilePaths.Resolve("exports", root));
        Assert.Equal(root, ProfilePaths.Resolve(root, Path.GetTempPath()));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptyProfilePathsFail(string path) => Assert.Throws<ArgumentException>(() => ProfilePaths.Resolve(path, Path.GetTempPath()));

    [Theory]
    [InlineData("unknown", "--profile", "x")]
    [InlineData("mount", "--unknown", "x")]
    [InlineData("mount", "--profile")]
    [InlineData("inspect", "--profile", "x")]
    [InlineData("search", "--profile", "x", "--limit", "201")]
    [InlineData("search", "--profile", "x", "--offset", "-1")]
    [InlineData("mount", "--profile", "x", "--profile", "y")]
    public void InvalidArgumentsFail(params string[] args) => Assert.Throws<ArgumentException>(() => CliOptions.Parse(args));

    [Fact]
    public void SearchDefaultsAreBounded()
    {
        var options = CliOptions.Parse(["search", "--profile", "x"]);
        Assert.Equal(50, options.Number("limit", 50, 1, 200));
        Assert.Equal(0, options.Number("offset", 0, 0, int.MaxValue));
    }

    [Theory]
    [InlineData("containers")]
    [InlineData("list")]
    [InlineData("diff")]
    public void ReadCommandsRequireProfile(string command)
        => Assert.Throws<ArgumentException>(() => CliOptions.Parse([command]));

    [Theory]
    [InlineData("list", "--profile", "profile.json")]
    [InlineData("diff", "--profile", "profile.json", "--asset", "Game/asset.uasset")]
    [InlineData("diff", "--profile", "profile.json", "--container", "target.pak")]
    public void ContainerCommandsRejectMissingSelectors(params string[] args)
        => Assert.Throws<ArgumentException>(() => CliOptions.Parse(args));

    [Fact]
    public void ContainersAndListAcceptPagingAndQuery()
    {
        var containers = CliOptions.Parse(["containers", "--profile", "p", "--query", "patch", "--offset", "10", "--limit", "200"]);
        Assert.Equal("patch", containers.Optional("query"));
        Assert.Equal(10, containers.Number("offset", 0, 0, int.MaxValue));
        Assert.Equal(200, containers.Number("limit", 50, 1, 200));
        var list = CliOptions.Parse(["list", "--profile", "p", "--container", "D:\\Game\\patch.pak", "--query", "table", "--offset", "0", "--limit", "1"]);
        Assert.Equal("D:\\Game\\patch.pak", list.Required("container"));
        Assert.Equal("table", list.Optional("query"));
    }

    [Theory]
    [InlineData("containers")]
    [InlineData("list")]
    public void ContainerPagingRejectsOutOfRangeValues(string command)
    {
        var prefix = command == "list" ? new[] { command, "--profile", "p", "--container", "a.pak" } : [command, "--profile", "p"];
        foreach (var value in new[] { "0", "201", "not-a-number" })
            Assert.Throws<ArgumentException>(() => CliOptions.Parse([.. prefix, "--limit", value]));
        Assert.Throws<ArgumentException>(() => CliOptions.Parse([.. prefix, "--offset", "-1"]));
    }

    [Fact]
    public void DiffAcceptsAutomaticAndExplicitPreviousContainers()
    {
        string[] prefix = ["diff", "--profile", "p", "--container", "target.pak", "--asset", "Game/table.uasset"];
        Assert.Null(CliOptions.Parse(prefix).Optional("against"));
        var explicitVersion = CliOptions.Parse([.. prefix, "--against", "D:\\Game\\base.pak", "--max-differences", "1000", "--max-depth", "64", "--max-nodes", "2000000"]);
        Assert.Equal("D:\\Game\\base.pak", explicitVersion.Optional("against"));
        Assert.Equal("1000", explicitVersion.Optional("max-differences"));
        Assert.Equal("64", explicitVersion.Optional("max-depth"));
        Assert.Equal("2000000", explicitVersion.Optional("max-nodes"));
    }

    [Theory]
    [InlineData("max-differences", "0")]
    [InlineData("max-differences", "1001")]
    [InlineData("max-depth", "0")]
    [InlineData("max-depth", "65")]
    [InlineData("max-nodes", "0")]
    [InlineData("max-nodes", "2000001")]
    [InlineData("max-nodes", "2147483648")]
    [InlineData("max-nodes", "no")]
    public void DiffRejectsOutOfRangeBudgets(string option, string value)
        => Assert.Throws<ArgumentException>(() => CliOptions.Parse(["diff", "--profile", "p", "--container", "a.pak", "--asset", "x.uasset", "--" + option, value]));

    [Theory]
    [InlineData("inspect")]
    [InlineData("extract")]
    public void ExportCommandsAcceptOutputDirectoryOverride(string command)
    {
        var options = CliOptions.Parse([command, "--profile", "original.json", "--asset", "Game/a.uasset", "--output-directory", "D:\\exports"]);
        Assert.Equal("D:\\exports", options.Optional("output-directory"));
        Assert.Equal("original.json", options.Required("profile"));
    }

    [Theory]
    [InlineData("containers")]
    [InlineData("list")]
    [InlineData("diff")]
    public void PureReadCommandsRejectExportOptions(string command)
        => Assert.Throws<ArgumentException>(() => CliOptions.Parse([command, "--profile", "p", "--output-directory", "out"]));

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/../../escape")]
    [InlineData("/escape")]
    [InlineData("C:\\escape")]
    [InlineData("a:stream")]
    [InlineData("a\\..\\escape")]
    public void UnsafePathsFail(string relative) => Assert.Throws<IOException>(() => OutputFiles.Resolve(Path.GetTempPath(), relative));

    [Fact]
    public void PrefixSiblingIsNotInside() => Assert.False(OutputFiles.IsWithin(Path.Combine(Path.GetTempPath(), "game-other"), Path.Combine(Path.GetTempPath(), "game")));

    [Fact]
    public void ExistingFileIsNeverOverwritten()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        try
        {
            OutputFiles.Write(path, s => s.WriteByte(42));
            Assert.Throws<IOException>(() => OutputFiles.Write(path, s => s.WriteByte(0)));
            Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FailedWriteIsRemoved()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        Assert.Throws<InvalidOperationException>(() => OutputFiles.Write(path, _ => throw new InvalidOperationException()));
        Assert.False(File.Exists(path));
    }
}
