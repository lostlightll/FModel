using FModel.Cli;
using FModel.Cli.Lua;

namespace FModel.Cli.Tests;

public sealed class LuaChunkReaderTests
{
    private static uint Abc(int op, int a = 0, int b = 0, int c = 0) => (uint)(op | a << 6 | b << 23 | c << 14);
    private static byte[] Chunk(uint[]? code = null, int nesting = 0, byte[]? constant = null, bool bigEndian = false, int sizeT = 8, int childUpvalues = 0)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        void I(int n) { var b = BitConverter.GetBytes(n); if (bigEndian) Array.Reverse(b); w.Write(b); }
        void L(long n) { var b = BitConverter.GetBytes(n); if (bigEndian) Array.Reverse(b); w.Write(b); }
        w.Write(new byte[] {27,76,117,97,83,0,25,147,13,10,26,10,4,(byte)sizeT,4,8,8});
        L(0x5678); L(BitConverter.DoubleToInt64Bits(370.5)); w.Write((byte)0);
        void P(int level)
        {
            w.Write((byte)0); I(0); I(0); w.Write(new byte[] {0,0,2});
            var instructions = code ?? [Abc(38, b: 1)]; I(instructions.Length); foreach (var word in instructions) I(unchecked((int)word));
            I(constant == null ? 0 : 1); if (constant != null) { w.Write((byte)4); w.Write((byte)(constant.Length + 1)); w.Write(constant); }
            var upvalues = level < nesting ? childUpvalues : 0;
            I(upvalues); for (var u = 0; u < upvalues; u++) w.Write(new byte[] {1,0});
            I(level > 0 ? 1 : 0); if (level > 0) P(level - 1); I(0); I(0); I(0);
        }
        P(nesting); return stream.ToArray();
    }
    [Theory]
    [InlineData(false,4)] [InlineData(false,8)] [InlineData(true,4)] [InlineData(true,8)]
    public void ReadsStandardHeaders(bool bigEndian, int sizeT)
        => Assert.Equal("lua53", LuaChunkReader.Read(Chunk(bigEndian: bigEndian, sizeT: sizeT)).Dialect);
    [Fact]
    public void BoundsChildUpvalueCountBeforeAllocating()
    {
        Assert.Equal(255, LuaChunkReader.Read(Chunk(nesting: 1, childUpvalues: 255), "lua53").Main.Children[0].Upvalues.Length);
        Assert.Equal("InvalidLuaBytecode", Assert.Throws<AnalysisException>(() => LuaChunkReader.Read(Chunk(nesting: 1, childUpvalues: 256), "lua53")).Code);
    }
    [Fact]
    public void ReadsTencentReturnPermutation()
    {
        Assert.Equal("RETURN", LuaOpcodes.Get("slua53", 27).Name);
        Assert.Equal("CLOSURE", LuaOpcodes.Get("slua53", 33).Name);
        Assert.Equal("slua53", LuaChunkReader.Read(Chunk([8388635])).Dialect);
        Assert.Throws<AnalysisException>(() => LuaChunkReader.Read(Chunk([8388635]), "lua53"));
    }
    [Fact]
    public void PreservesInvalidUtf8Bytes()
        => Assert.Equal(new byte[] {255,254}, Assert.IsType<byte[]>(LuaChunkReader.Read(Chunk(constant: [255,254])).Main.Constants[0].Value));
    [Fact]
    public void RejectsTruncatedAndTrailingData()
    {
        var data = Chunk();
        for (var n = 0; n < data.Length; n++) Assert.Throws<AnalysisException>(() => LuaChunkReader.Read(data[..n]));
        Assert.Throws<AnalysisException>(() => LuaChunkReader.Read([..data,0]));
    }
    [Fact]
    public void RejectsUnknownOpcodeAndClosureOutsideChildren()
    {
        Assert.Throws<AnalysisException>(() => LuaChunkReader.Read(Chunk([63,Abc(38,b:1)]), "lua53"));
        Assert.Throws<AnalysisException>(() => LuaChunkReader.Read(Chunk([Abc(44),Abc(38,b:1)]), "lua53"));
        Assert.Throws<AnalysisException>(() => LuaChunkReader.Read(Chunk([Abc(33) | (133122u << 14),Abc(27,b:1)]), "slua53"));
    }
    [Fact]
    public void RejectsOverrunAndResourceLimits()
    {
        var data = Chunk(); Array.Fill(data, (byte)255, 46, 4);
        Assert.Throws<AnalysisException>(() => LuaChunkReader.Read(data));
        Assert.Equal("LuaResourceLimit", Assert.Throws<AnalysisException>(() => LuaChunkReader.Read(Chunk(nesting:64))).Code);
        Assert.Equal("LuaResourceLimit", Assert.Throws<AnalysisException>(() => LuaChunkReader.Read(new byte[LuaChunkReader.MaxFileBytes+1])).Code);
    }
    [Theory]
    [InlineData(2)] // missing LOADKX extra arg
    [InlineData(46)] // orphan EXTRAARG
    [InlineData(31)] // comparison must be followed by jump
    public void RejectsBrokenInstructionPairs(int op)
        => Assert.Throws<AnalysisException>(() => LuaChunkReader.Read(Chunk([Abc(op),Abc(38,b:1)]), "lua53"));
    [Fact]
    public void RejectsRegisterAndJumpBounds()
    {
        Assert.Throws<AnalysisException>(() => LuaChunkReader.Read(Chunk([Abc(0,a:2),Abc(38,b:1)]), "lua53"));
        Assert.Throws<AnalysisException>(() => LuaChunkReader.Read(Chunk([Abc(30),Abc(38,b:1)]), "lua53"));
    }
}

