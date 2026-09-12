using System.Buffers.Binary;
using System.Text;

namespace FModel.Cli.Lua;

internal static class LuaChunkReader
{
    public const int MaxFileBytes = 4 * 1024 * 1024;
    public const int MaxFunctions = 4096;
    public const int MaxInstructions = 250000;
    public const int MaxDepth = 64;

    public static LuaChunk Read(byte[] data, string dialect = "auto")
    {
        if (data.Length > MaxFileBytes) throw new AnalysisException("LuaResourceLimit", "Lua chunk exceeds byte limit.");
        if (dialect is not ("auto" or "lua53" or "slua53")) throw new AnalysisException("UnsupportedLuaDialect", "Unsupported Lua dialect.");
        var main = new Reader(data).Read();
        if (dialect != "auto") { Validate(main, dialect, null); return new(dialect, main); }
        var valid = new List<string>();
        foreach (var candidate in new[] { "lua53", "slua53" })
        {
            try { Validate(main, candidate, null); valid.Add(candidate); }
            catch (AnalysisException) { }
        }
        if (valid.Count == 0) throw new AnalysisException("UnsupportedLuaDialect", "Bytecode does not validate against a supported opcode mapping.");
        if (valid.Count != 1) throw new AnalysisException("AmbiguousLuaDialect", "Bytecode validates against multiple mappings; specify a dialect explicitly.");
        return new(valid[0], main);
    }

    private static void Invalid() => throw new AnalysisException("InvalidLuaBytecode", "Malformed Lua bytecode or invalid opcode operands.");
    private static void Require(bool value) { if (!value) Invalid(); }

    private static void Validate(LuaPrototype p, string dialect, LuaPrototype? parent)
    {
        Require(p.MaxStack >= 2 && p.Parameters <= p.MaxStack && p.Vararg <= 1 && p.Code.Length > 0);
        foreach (var u in p.Upvalues)
            Require(u.InStack <= 1 && (parent == null || u.Index < (u.InStack == 1 ? parent.MaxStack : parent.Upvalues.Length)));
        var instructions = p.Code.Select(LuaOpcodes.Decode).ToArray();
        var ops = instructions.Select(i => LuaOpcodes.Get(dialect, i.Opcode)).ToArray();
        void Reg(int r) => Require(r >= 0 && r < p.MaxStack);
        void Constant(int k) => Require(k >= 0 && k < p.Constants.Length);
        void Up(int u) => Require(u >= 0 && u < p.Upvalues.Length);
        void Rk(int x) { if (x >= 256) Constant(x & 255); else Reg(x); }
        void Target(int pc) => Require(pc >= 0 && pc < ops.Length && ops[pc].Name != "EXTRAARG");
        for (var pc = 0; pc < instructions.Length; pc++)
        {
            var i = instructions[pc]; var op = ops[pc]; var n = op.Name;
            if (op.Mode == "ABC")
            {
                if (op.BMode == "N") Require(i.B == 0);
                if (op.CMode == "N") Require(i.C == 0);
                if (op.BMode == "R") Reg(i.B);
                if (op.CMode == "R") Reg(i.C);
                if (op.BMode == "K") Rk(i.B);
                if (op.CMode == "K") Rk(i.C);
            }
            if (op.WritesA || n is "SETUPVAL" or "SETTABLE" or "TEST" or "RETURN" or "TFORCALL" or "SETLIST") Reg(i.A);
            switch (n)
            {
                case "LOADK": Constant(i.Bx); break;
                case "LOADKX":
                    Require(i.Bx == 0 && pc + 1 < ops.Length && ops[pc + 1].Name == "EXTRAARG");
                    Constant(instructions[pc + 1].Ax); break;
                case "EXTRAARG":
                    Require(pc > 0 && (ops[pc - 1].Name == "LOADKX" || (ops[pc - 1].Name == "SETLIST" && instructions[pc - 1].C == 0))); break;
                case "LOADBOOL": Require(i.B <= 1 && i.C <= 1); if (i.C != 0) Target(pc + 2); break;
                case "LOADNIL": Reg(i.A + i.B); break;
                case "GETUPVAL": case "SETUPVAL": case "GETTABUP": Up(i.B); break;
                case "SETTABUP": Up(i.A); break;
                case "SELF": Reg(i.A + 1); break;
                case "CONCAT": Require(i.B <= i.C); break;
                case "JMP": Require(i.A <= p.MaxStack); Target(pc + 1 + i.SBx); break;
                case "EQ": case "LT": case "LE":
                    Require(i.A <= 1); goto case "TEST";
                case "TEST": case "TESTSET":
                    if (n is "TEST" or "TESTSET") Require(i.C <= 1);
                    Require(pc + 1 < ops.Length && ops[pc + 1].Name == "JMP"); Target(pc + 2); break;
                case "CALL": case "TAILCALL":
                    if (i.B > 0) Reg(i.A + i.B - 1);
                    if (i.C > 1) Reg(i.A + i.C - 2);
                    if (n == "TAILCALL") Require(i.C == 0); break;
                case "RETURN": if (i.B > 1) Reg(i.A + i.B - 2); break;
                case "FORLOOP": case "FORPREP": Reg(i.A + 3); Target(pc + 1 + i.SBx); break;
                case "TFORCALL":
                    Reg(i.A + 2 + i.C); Require(i.C > 0 && pc + 1 < ops.Length && ops[pc + 1].Name == "TFORLOOP"); break;
                case "TFORLOOP": Reg(i.A + 1); Target(pc + 1 + i.SBx); break;
                case "SETLIST":
                    if (i.B > 0) Reg(i.A + i.B);
                    if (i.C == 0) Require(pc + 1 < ops.Length && ops[pc + 1].Name == "EXTRAARG" && instructions[pc + 1].Ax > 0); break;
                case "CLOSURE": Require(i.Bx < p.Children.Length); break;
                case "VARARG": Require(p.Vararg != 0); if (i.B > 1) Reg(i.A + i.B - 2); break;
            }
        }
        Require(ops[^1].Name == "RETURN");
        foreach (var child in p.Children) Validate(child, dialect, p);
    }

    private sealed class Reader(byte[] data)
    {
        private int _position, _functions, _instructions, _sizeT;
        private bool _little;
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private ReadOnlySpan<byte> Bytes(int count)
        {
            Require(count >= 0 && count <= data.Length - _position);
            var result = data.AsSpan(_position, count); _position += count; return result;
        }
        private byte Byte() => Bytes(1)[0];
        private uint U32() { var bytes = Bytes(4); return _little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes); }
        private ulong U64() { var bytes = Bytes(8); return _little ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : BinaryPrimitives.ReadUInt64BigEndian(bytes); }
        private int Int() { var n = U32(); Require(n <= int.MaxValue); return (int)n; }
        private int Count(int minimumBytes)
        {
            var count = Int(); Require(count <= (data.Length - _position) / minimumBytes); return count;
        }
        private byte[]? StringBytes()
        {
            ulong length = Byte(); if (length == 0) return null;
            if (length == 255) length = _sizeT == 4 ? U32() : U64();
            Require(length > 0 && length - 1 <= (ulong)(data.Length - _position));
            return Bytes((int)(length - 1)).ToArray();
        }
        private static object Decode(byte[] bytes)
        {
            try { return Utf8.GetString(bytes); } catch (DecoderFallbackException) { return bytes; }
        }
        private string? String()
        {
            var bytes = StringBytes(); if (bytes == null) return null;
            return Decode(bytes) is string text ? text : "hex:" + Convert.ToHexString(bytes);
        }
        public LuaPrototype Read()
        {
            if (!Bytes(4).SequenceEqual(new byte[] { 27, 76, 117, 97 }))
                throw new AnalysisException("UnsupportedLuaFormat", "Expected a Lua 5.3 binary chunk; LuaJIT and source text are unsupported.");
            if (Byte() != 0x53 || Byte() != 0 || !Bytes(6).SequenceEqual(new byte[] { 25, 147, 13, 10, 26, 10 }))
                throw new AnalysisException("UnsupportedLuaFormat", "Unsupported Lua header.");
            var intSize = Byte(); _sizeT = Byte(); var instructionSize = Byte(); var integerSize = Byte(); var numberSize = Byte();
            Require(intSize == 4 && _sizeT is 4 or 8 && instructionSize == 4 && integerSize == 8 && numberSize == 8);
            var marker = Bytes(8);
            if (BinaryPrimitives.ReadUInt64LittleEndian(marker) == 0x5678) _little = true;
            else if (BinaryPrimitives.ReadUInt64BigEndian(marker) == 0x5678) _little = false;
            else Invalid();
            Require(BitConverter.Int64BitsToDouble((long)U64()) == 370.5);
            var upvalues = Byte(); var main = Prototype(0);
            Require(main.Upvalues.Length == upvalues && _position == data.Length); return main;
        }
        private LuaPrototype Prototype(int depth)
        {
            if (depth >= MaxDepth || ++_functions > MaxFunctions) throw new AnalysisException("LuaResourceLimit", "Lua function count or nesting limit exceeded.");
            var source = String(); var first = Int(); var last = Int(); var parameters = Byte(); var vararg = Byte(); var stack = Byte();
            var nc = Count(4); _instructions += nc;
            if (_instructions > MaxInstructions) throw new AnalysisException("LuaResourceLimit", "Lua instruction limit exceeded.");
            var code = new uint[nc]; for (var i = 0; i < nc; i++) code[i] = U32();
            var constants = new LuaConstant[Count(1)];
            for (var i = 0; i < constants.Length; i++)
            {
                var tag = Byte(); object? value;
                switch (tag)
                {
                    case 0: value = null; break;
                    case 1: var b = Byte(); Require(b <= 1); value = b == 1; break;
                    case 3: value = BitConverter.Int64BitsToDouble((long)U64()); break;
                    case 19: value = unchecked((long)U64()); break;
                    case 4: case 20: var bytes = StringBytes(); Require(bytes != null); value = Decode(bytes!); break;
                    default: Invalid(); return null!;
                }
                constants[i] = new(tag, value);
            }
            var upvalueCount = Count(2); Require(upvalueCount <= 255);
            var upvalues = new LuaUpvalue[upvalueCount]; for (var i = 0; i < upvalues.Length; i++) upvalues[i] = new(Byte(), Byte());
            var childCount = Count(40);
            if (childCount > MaxFunctions - _functions) throw new AnalysisException("LuaResourceLimit", "Lua function limit exceeded.");
            var children = new LuaPrototype[childCount]; for (var i = 0; i < childCount; i++) children[i] = Prototype(depth + 1);
            var lineCount = Count(4); Require(lineCount == 0 || lineCount == code.Length);
            var lines = new int[lineCount];
            for (var i = 0; i < lines.Length; i++) lines[i] = Int();
            var locals = new LuaLocal[Count(9)];
            for (var i = 0; i < locals.Length; i++)
            {
                var name = String(); var start = Int(); var end = Int(); Require(name != null && start <= end && end <= code.Length);
                locals[i] = new(name!, start, end);
            }
            var nameCount = Count(1); Require(nameCount == 0 || nameCount == upvalues.Length);
            var names = new string[nameCount];
            for (var i = 0; i < names.Length; i++) { var name = String(); Require(name != null); names[i] = name!; }
            return new() { Source = source, FirstLine = first, LastLine = last, Parameters = parameters, Vararg = vararg, MaxStack = stack,
                Code = code, Constants = constants, Upvalues = upvalues, Children = children, Lines = lines, Locals = locals, UpvalueNames = names };
        }
    }
}

