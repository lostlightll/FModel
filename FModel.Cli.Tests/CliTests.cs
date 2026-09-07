using FModel.Cli;

public sealed class CliTests
{
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
