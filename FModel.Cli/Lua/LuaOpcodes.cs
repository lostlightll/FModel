namespace FModel.Cli.Lua;

// Opcode metadata and slua permutation: see OPCODE-SOURCES.md.
internal static class LuaOpcodes
{
    private static readonly LuaOpcode[] Standard =
    [
        new("MOVE", "ABC", "R", "N", true),
        new("LOADK", "ABx", "K", "N", true),
        new("LOADKX", "ABx", "N", "N", true),
        new("LOADBOOL", "ABC", "U", "U", true),
        new("LOADNIL", "ABC", "U", "N", true),
        new("GETUPVAL", "ABC", "U", "N", true),
        new("GETTABUP", "ABC", "U", "K", true),
        new("GETTABLE", "ABC", "R", "K", true),
        new("SETTABUP", "ABC", "K", "K", false),
        new("SETUPVAL", "ABC", "U", "N", false),
        new("SETTABLE", "ABC", "K", "K", false),
        new("NEWTABLE", "ABC", "U", "U", true),
        new("SELF", "ABC", "R", "K", true),
        new("ADD", "ABC", "K", "K", true),
        new("SUB", "ABC", "K", "K", true),
        new("MUL", "ABC", "K", "K", true),
        new("MOD", "ABC", "K", "K", true),
        new("POW", "ABC", "K", "K", true),
        new("DIV", "ABC", "K", "K", true),
        new("IDIV", "ABC", "K", "K", true),
        new("BAND", "ABC", "K", "K", true),
        new("BOR", "ABC", "K", "K", true),
        new("BXOR", "ABC", "K", "K", true),
        new("SHL", "ABC", "K", "K", true),
        new("SHR", "ABC", "K", "K", true),
        new("UNM", "ABC", "R", "N", true),
        new("BNOT", "ABC", "R", "N", true),
        new("NOT", "ABC", "R", "N", true),
        new("LEN", "ABC", "R", "N", true),
        new("CONCAT", "ABC", "R", "R", true),
        new("JMP", "AsBx", "R", "N", false),
        new("EQ", "ABC", "K", "K", false),
        new("LT", "ABC", "K", "K", false),
        new("LE", "ABC", "K", "K", false),
        new("TEST", "ABC", "N", "U", false),
        new("TESTSET", "ABC", "R", "U", true),
        new("CALL", "ABC", "U", "U", true),
        new("TAILCALL", "ABC", "U", "U", true),
        new("RETURN", "ABC", "U", "N", false),
        new("FORLOOP", "AsBx", "R", "N", true),
        new("FORPREP", "AsBx", "R", "N", true),
        new("TFORCALL", "ABC", "N", "U", false),
        new("TFORLOOP", "AsBx", "R", "N", true),
        new("SETLIST", "ABC", "U", "U", false),
        new("CLOSURE", "ABx", "U", "N", true),
        new("VARARG", "ABC", "U", "N", true),
        new("EXTRAARG", "Ax", "U", "U", false),
    ];
    private static readonly string[] SluaOrder =
    [
        "MOVE", "SELF", "ADD", "SUB", "MUL", "MOD", "POW", "DIV", "IDIV", "BAND", "BOR", "BXOR", "SHL", "SHR", "UNM", "BNOT", "NOT", "LEN", "CONCAT", "JMP", "EQ", "LT", "LE", "TEST", "TESTSET", "CALL", "TAILCALL", "RETURN", "FORLOOP", "FORPREP", "TFORCALL", "TFORLOOP", "SETLIST", "CLOSURE", "VARARG", "LOADK", "LOADKX", "LOADBOOL", "LOADNIL", "GETUPVAL", "GETTABUP", "GETTABLE", "SETTABUP", "SETUPVAL", "SETTABLE", "NEWTABLE", "EXTRAARG"
    ];
    private static readonly LuaOpcode[] Slua = SluaOrder.Select(n => Standard.Single(o => o.Name == n)).ToArray();
    public static LuaOpcode Get(string dialect, int opcode)
    {
        var map = dialect switch { "lua53" => Standard, "slua53" => Slua, _ => throw new AnalysisException("UnsupportedLuaDialect", "Unsupported Lua dialect.") };
        if ((uint)opcode >= map.Length) throw new AnalysisException("InvalidLuaBytecode", "Unknown opcode.");
        return map[opcode];
    }
    public static LuaInstruction Decode(uint word) => new((int)(word & 63), (int)(word >> 6 & 255),
        (int)(word >> 23 & 511), (int)(word >> 14 & 511), (int)(word >> 14), (int)(word >> 14) - 131071, (int)(word >> 6));
}
