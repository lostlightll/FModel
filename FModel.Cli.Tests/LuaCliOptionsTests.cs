using FModel.Cli;

public sealed class LuaCliOptionsTests
{
    [Theory]
    [InlineData("lua-functions")]
    [InlineData("lua-diff")]
    public void ReadOnlyCommandsUseOriginalProfile(string command)
    {
        var parsed = CliOptions.Parse([command, "--profile", "private.json", "--container", "sample.pak", "--asset", "Game/script.luac"]);
        Assert.Null(parsed.Optional("output-directory"));
        Assert.Null(parsed.Optional("dialect"));
        Assert.Equal(20, parsed.Number("limit", 20, 1, 200));
    }

    [Fact]
    public void ExactFunctionIdRemainsLiteral()
    {
        const string id = "$/field:local:Module~1string:\"Method\"";
        var parsed = CliOptions.Parse(["lua-disasm", "--profile", "p", "--container", "c", "--asset", "a", "--function", id]);
        Assert.Equal(id, parsed.Required("function"));
    }

    [Theory]
    [InlineData("lua-diff", "--max-changes", "0")]
    [InlineData("lua-diff", "--max-changes", "1001")]
    [InlineData("lua-diff", "--max-work", "0")]
    [InlineData("lua-diff", "--max-work", "10000001")]
    [InlineData("lua-diff", "--query", "text")]
    [InlineData("lua-functions", "--dialect", "luajit")]
    [InlineData("lua-functions", "--limit", "201")]
    [InlineData("lua-functions", "--offset", "-1")]
    public void UnsupportedOrUnboundedOptionsFail(string command, string option, string value) =>
        Assert.Throws<ArgumentException>(() => CliOptions.Parse([command, "--profile", "p", "--container", "c", "--asset", "a", option, value]));

    [Fact]
    public void MissingContainerAssetOrFunctionFails()
    {
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["lua-functions", "--profile", "p", "--asset", "a"]));
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["lua-functions", "--profile", "p", "--container", "c"]));
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["lua-disasm", "--profile", "p", "--container", "c", "--asset", "a"]));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("lua53")]
    [InlineData("slua53")]
    public void DialectAndMaximumBudgetsAreAccepted(string dialect)
    {
        var parsed = CliOptions.Parse(["lua-diff", "--profile", "p", "--container", "c", "--asset", "a", "--dialect", dialect,
            "--max-changes", "1000", "--max-work", "10000000"]);
        Assert.Equal(dialect, parsed.Required("dialect"));
    }

    [Fact]
    public void OpcodeLicenseShipsInsideAssembly()
    {
        using var stream = typeof(GameProfile).Assembly.GetManifestResourceStream("FModel.Cli.Lua.OPCODE-SOURCES.md");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        Assert.Contains("Permission is hereby granted", reader.ReadToEnd());
    }
}
