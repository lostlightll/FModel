namespace FModel.Cli.Lua;

internal sealed record LuaChunk(string Dialect, LuaPrototype Main);
internal sealed class LuaPrototype
{
    public string? Source { get; init; }
    public int FirstLine { get; init; }
    public int LastLine { get; init; }
    public int Parameters { get; init; }
    public int Vararg { get; init; }
    public int MaxStack { get; init; }
    public uint[] Code { get; init; } = [];
    public LuaConstant[] Constants { get; init; } = [];
    public LuaUpvalue[] Upvalues { get; init; } = [];
    public LuaPrototype[] Children { get; init; } = [];
    public int[] Lines { get; init; } = [];
    public LuaLocal[] Locals { get; init; } = [];
    public string[] UpvalueNames { get; init; } = [];
}
internal sealed record LuaConstant(byte Tag, object? Value);
internal sealed record LuaUpvalue(byte InStack, byte Index);
internal sealed record LuaLocal(string Name, int StartPc, int EndPc);
internal sealed record LuaInstruction(int Opcode, int A, int B, int C, int Bx, int SBx, int Ax);
internal sealed record LuaOpcode(string Name, string Mode, string BMode, string CMode, bool WritesA);
