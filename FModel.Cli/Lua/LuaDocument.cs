using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace FModel.Cli.Lua;

[JsonObject(NamingStrategyType = typeof(Newtonsoft.Json.Serialization.CamelCaseNamingStrategy))]
internal sealed record LuaDisassembly(int Pc, int? Line, string Opcode, string[] Operands, string Normalized, int? TargetPc, bool PreviewTruncated);
internal sealed record LuaFunction(string Id, string Identity, LuaPrototype Prototype, string[] Captures, LuaDisassembly[] Instructions);

/// <summary>A data-only view. Identities are proven assignments/call sites or exact structural identities.</summary>
internal sealed class LuaDocument
{
    public IReadOnlyDictionary<string, LuaFunction> Functions => _functions;
    public bool HasUnresolvedChildIdentities { get; private set; }
    private readonly Dictionary<string, LuaFunction> _functions = new(StringComparer.Ordinal);
    private readonly string _dialect;
    private readonly bool _rootOnly;
    private int _characters;
    private long _identityWork;
    public LuaDocument(LuaChunk chunk, bool rootOnly = false)
    {
        _dialect = chunk.Dialect;
        _rootOnly = rootOnly;
        Visit(chunk.Main, "$", "root", Enumerable.Range(0, chunk.Main.Upvalues.Length)
            .Select(i => "root-upvalue:" + i).ToArray());
    }

    private sealed class Symbol(string? text = null, Table? table = null, int? closure = null)
    {
        public string? Text = text;
        public Table? Table = table;
        public int? Closure = closure;
    }
    private sealed class Table
    {
        public string? Label;
        public Symbol? Parent;
        public string? Key;
        public bool ArrayElement;
        public Dictionary<string, Symbol> Fields = new(StringComparer.Ordinal);
    }
    private sealed record Role(Symbol Owner, string Key);
    private sealed record Discovery(string[] Identities, int[] ClosurePcs);

    private Discovery Discover(LuaPrototype p, string[] captures, string[] constants)
    {
        _identityWork += (long)p.Code.Length * (p.Locals.Length + p.MaxStack + 1) + p.Children.Sum(c => (long)c.Upvalues.Length * (p.Locals.Length + 1));
        if (_identityWork > 20000000) throw new AnalysisException("LuaResourceLimit", "Function identity analysis exceeds work limit.");
        var registers = new Dictionary<int, Symbol>();
        var fields = new Dictionary<int, Role>();
        var locals = new Dictionary<int, string>();
        var callbacks = new Dictionary<int, string>();
        var closurePcs = Enumerable.Repeat(-1, p.Children.Length).ToArray();
        Symbol R(int r, int pc) => registers.GetValueOrDefault(r) ?? new Symbol(RegisterName(p, r, pc));
        Symbol K(int k) => new(constants[k]);
        Symbol RK(int v, int pc) => v >= 256 ? K(v & 255) : R(v, pc);
        string Key(Symbol s) => Resolve(s);
        void RoleFor(int child, Role role)
        {
            if (fields.TryGetValue(child, out var prior) && (prior.Owner != role.Owner || prior.Key != role.Key))
                throw new AnalysisException("AmbiguousLuaFunctionIdentity", "A prototype has multiple field assignments.");
            fields[child] = role;
        }
        var targets = new HashSet<int>();
        for (var pc = 0; pc < p.Code.Length; pc++)
        {
            var i = LuaOpcodes.Decode(p.Code[pc]); var op = LuaOpcodes.Get(_dialect, i.Opcode);
            if (op.Mode == "AsBx") targets.Add(pc + 1 + i.SBx);
            if (op.Name is "TEST" or "TESTSET" or "EQ" or "LT" or "LE" || op.Name == "LOADBOOL" && i.C != 0) targets.Add(pc + 2);
        }
        for (var pc = 0; pc < p.Code.Length; pc++)
        {
            if (targets.Contains(pc)) registers.Clear(); // never infer an assignment across a control-flow merge
            var i = LuaOpcodes.Decode(p.Code[pc]); var op = LuaOpcodes.Get(_dialect, i.Opcode);
            switch (op.Name)
            {
                case "MOVE": registers[i.A] = R(i.B, pc); break;
                case "LOADK": registers[i.A] = K(i.Bx); break;
                case "LOADKX": registers[i.A] = K(LuaOpcodes.Decode(p.Code[pc + 1]).Ax); break;
                case "GETUPVAL": registers[i.A] = new(captures[i.B]); break;
                case "GETTABUP": registers[i.A] = new(captures[i.B] + "/" + Key(RK(i.C, pc))); break;
                case "GETTABLE":
                {
                    var owner = R(i.B, pc); var key = Key(RK(i.C, pc));
                    registers[i.A] = owner.Table?.Fields.GetValueOrDefault(key) ?? new Symbol(Resolve(owner) + "/" + key);
                    break;
                }
                case "NEWTABLE": registers[i.A] = new(table: new Table()); break;
                case "CLOSURE":
                    if (closurePcs[i.Bx] >= 0) throw new AnalysisException("AmbiguousLuaFunctionIdentity", "A prototype is instantiated at multiple sites.");
                    closurePcs[i.Bx] = pc; registers[i.A] = new(closure: i.Bx); break;
                case "SETTABLE":
                case "SETTABUP":
                {
                    var owner = op.Name == "SETTABLE" ? R(i.A, pc) : new Symbol(captures[i.A]);
                    var key = Key(RK(i.B, pc)); var value = RK(i.C, pc);
                    if (value.Closure is { } child) RoleFor(child, new(owner, key));
                    if (value.Table is { Parent: null } table && !ReferenceEquals(owner.Table, table)) { table.Parent = owner; table.Key = key; }
                    if (owner.Table is { } parent) parent.Fields[key] = value;
                    break;
                }
                case "SETLIST":
                    for (var n = 1; n <= i.B; n++)
                        if (R(i.A + n, pc).Table is { } table) { table.Parent = R(i.A, pc); table.ArrayElement = true; table.Key = "array-item"; }
                    break;
                case "SELF":
                {
                    var owner = R(i.B, pc); registers[i.A + 1] = owner;
                    registers[i.A] = new(Resolve(owner) + "/" + Key(RK(i.C, pc))); break;
                }
                case "CALL":
                case "TAILCALL":
                {
                    var call = Resolve(R(i.A, pc));
                    for (var n = 1; n < i.B; n++)
                        if (R(i.A + n, pc).Closure is { } child)
                        {
                            var identity = "callback:" + call + "/argument:" + n;
                            if (callbacks.TryGetValue(child, out var prior) && prior != identity)
                                throw new AnalysisException("AmbiguousLuaFunctionIdentity", "A closure is passed to multiple call sites.");
                            callbacks[child] = identity;
                        }
                    foreach (var r in registers.Keys.Where(r => r >= i.A).ToArray()) registers.Remove(r);
                    break;
                }
                case "LOADNIL":
                    for (var r = i.A; r <= i.A + i.B; r++) registers.Remove(r);
                    break;
                case "VARARG":
                    foreach (var r in registers.Keys.Where(r => r >= i.A && (i.B == 0 || r < i.A + i.B - 1)).ToArray()) registers.Remove(r);
                    break;
                case "TFORCALL":
                    for (var r = i.A + 3; r <= i.A + 2 + i.C; r++) registers.Remove(r);
                    break;
                default:
                    if (op.WritesA) registers.Remove(i.A);
                    break;
            }
            // Lua debug local start/end PCs are zero-based lifetime boundaries, not source line numbers.
            var active = p.Locals.Where(l => l.StartPc <= pc + 1 && pc + 1 < l.EndPc).ToArray();
            for (var r = 0; r < active.Length; r++)
                if (active[r].StartPc == pc + 1 && registers.TryGetValue(r, out var symbol))
                {
                    if (symbol.Closure is { } child) locals[child] = "local:" + Compact(active[r].Name);
                    if (symbol.Table is { } table) table.Label = "local:" + Compact(active[r].Name);
                }
            if (op.Mode == "AsBx" || op.Name is "RETURN" or "TAILCALL" or "TEST" or "TESTSET" or "EQ" or "LT" or "LE" || op.Name == "LOADBOOL" && i.C != 0)
                registers.Clear();
        }
        var identities = new string[p.Children.Length];
        for (var n = 0; n < identities.Length; n++)
        {
            if (closurePcs[n] < 0) throw new AnalysisException("AmbiguousLuaFunctionIdentity", "A child prototype has no closure site.");
            var binding = ChildCaptures(p, p.Children[n], captures, closurePcs[n]);
            if (fields.TryGetValue(n, out var role))
            {
                var owner = Resolve(role.Owner);
                identities[n] = "field:" + owner + "/" + role.Key;
                // Array positions are not stable identities. Distinguish callbacks by their referenced methods.
                if (owner.Contains("array-item", StringComparison.Ordinal)) identities[n] += "/" + CallSignature(p.Children[n], binding);
            }
            else if (locals.TryGetValue(n, out var local)) identities[n] = local;
            else if (callbacks.TryGetValue(n, out var callback)) identities[n] = callback;
            else identities[n] = "structural:" + Fingerprint(p.Children[n], binding);
        }
        if (!_rootOnly && identities.Distinct(StringComparer.Ordinal).Count() != identities.Length)
            throw new AnalysisException("AmbiguousLuaFunctionIdentity", "Sibling closures have indistinguishable identities; no index-based match was guessed. Use lua-disasm or lua-diff with --function $ to inspect only the root function.",
                identities.Select((name, index) => (name, index)).GroupBy(x => x.name).Where(g => g.Count() > 1).Take(20)
                    .Select(g => (object)new { identity = Compact(g.Key), prototypes = g.Select(x => x.index).Take(20).ToArray() }).ToArray());
        if (_rootOnly)
        {
            var duplicates = identities.GroupBy(s => s, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            HasUnresolvedChildIdentities = duplicates.Count > 0;
            // These indices describe root CLOSURE operands only; they are never used to pair child functions.
            for (var n = 0; n < identities.Length; n++)
                if (duplicates.Contains(identities[n])) identities[n] += "/unresolved-local-prototype:" + n;
        }
        return new(identities, closurePcs);
    }

    private static string Resolve(Symbol s, int depth = 0)
    {
        if (depth > 64) throw new AnalysisException("LuaResourceLimit", "Table identity nesting exceeds limit.");
        if (s.Table is not { } t) return Compact(s.Text ?? "anonymous");
        if (t.Label is not null) return t.Label;
        return t.Parent is null ? "anonymous-table" : Compact(Resolve(t.Parent, depth + 1) + "/" + t.Key);
    }
    private string CallSignature(LuaPrototype p, string[] captures)
    {
        _identityWork += (long)p.Code.Length * (p.MaxStack + 1);
        if (_identityWork > 20000000) throw new AnalysisException("LuaResourceLimit", "Function identity analysis exceeds work limit.");
        // A semantic signature is only an identity when unique among siblings; it is never an ordinal.
        var methods = new List<string>(); var registers = new Dictionary<int, string>();
        string R(int r) => registers.GetValueOrDefault(r) ?? "R" + r;
        string RK(int r) => r >= 256 ? Constant(p.Constants[r & 255]) : R(r);
        var targets = new HashSet<int>();
        for (var pc = 0; pc < p.Code.Length; pc++)
        {
            var instruction = LuaOpcodes.Decode(p.Code[pc]); var opcode = LuaOpcodes.Get(_dialect, instruction.Opcode);
            if (opcode.Mode == "AsBx") targets.Add(pc + 1 + instruction.SBx);
            if (opcode.Name is "TEST" or "TESTSET" or "EQ" or "LT" or "LE" || opcode.Name == "LOADBOOL" && instruction.C != 0) targets.Add(pc + 2);
        }
        for (var pc = 0; pc < p.Code.Length; pc++)
        {
            if (targets.Contains(pc)) registers.Clear();
            var i = LuaOpcodes.Decode(p.Code[pc]); var op = LuaOpcodes.Get(_dialect, i.Opcode);
            switch (op.Name)
            {
                case "GETUPVAL": registers[i.A] = captures[i.B]; break;
                case "GETTABUP": registers[i.A] = Compact(captures[i.B] + "/" + RK(i.C)); break;
                case "GETTABLE": registers[i.A] = Compact(R(i.B) + "/" + RK(i.C)); break;
                case "MOVE": registers[i.A] = R(i.B); break;
                case "LOADK": registers[i.A] = Constant(p.Constants[i.Bx]); break;
                case "SELF": registers[i.A + 1] = R(i.B); registers[i.A] = Compact(R(i.B) + "/" + RK(i.C)); break;
                case "CALL": case "TAILCALL":
                    var call = Compact(R(i.A) + "(" + string.Join(",", Enumerable.Range(1, Math.Max(0, i.B - 1)).Select(n => R(i.A + n))) + ")");
                    if (methods.Count == 4096) throw new AnalysisException("LuaResourceLimit", "Anonymous callback signature exceeds call-site limit.");
                    methods.Add(call);
                    foreach (var r in registers.Keys.Where(r => r >= i.A).ToArray()) registers.Remove(r);
                    if (i.C > 1) registers[i.A] = "result:" + call;
                    break;
                case "LOADNIL":
                    for (var r = i.A; r <= i.A + i.B; r++) registers.Remove(r);
                    break;
                case "VARARG":
                    foreach (var r in registers.Keys.Where(r => r >= i.A && (i.B == 0 || r < i.A + i.B - 1)).ToArray()) registers.Remove(r);
                    break;
                case "TFORCALL":
                    for (var r = i.A + 3; r <= i.A + 2 + i.C; r++) registers.Remove(r);
                    break;
                default: if (op.WritesA) registers.Remove(i.A); break;
            }
            if (op.Mode == "AsBx" || op.Name is "RETURN" or "TAILCALL" or "TEST" or "TESTSET" or "EQ" or "LT" or "LE" || op.Name == "LOADBOOL" && i.C != 0) registers.Clear();
        }
        return methods.Count > 0 ? "calls:" + Compact(string.Join(";", methods)) : "structural:" + Fingerprint(p, captures);
    }
    private string Fingerprint(LuaPrototype p, string[] captures)
    {
        var c = p.Constants.Select(Constant).ToArray();
        var children = Enumerable.Range(0, p.Children.Length).Select(i => "child:" + i).ToArray();
        return Hash(string.Join('\n', Render(p, captures, c, children).Select(i => i.Normalized)));
    }

    private void Visit(LuaPrototype p, string id, string identity, string[] captures)
    {
        if (id.Length > 4096) throw new AnalysisException("LuaResourceLimit", "Function identity exceeds limit.");
        var constants = p.Constants.Select(Constant).ToArray();
        var discovery = Discover(p, captures, constants);
        var childIds = discovery.Identities.Select(s => id + "/" + Escape(s)).ToArray();
        var instructions = Render(p, captures, constants, childIds);
        _functions.Add(id, new(id, identity, p, captures, instructions));
        for (var i = 0; !_rootOnly && i < p.Children.Length; i++)
            Visit(p.Children[i], childIds[i], discovery.Identities[i], ChildCaptures(p, p.Children[i], captures, discovery.ClosurePcs[i]));
    }

    private static string RegisterName(LuaPrototype p, int register, int pc)
    {
        var locals = p.Locals.Where(l => l.StartPc <= pc && pc < l.EndPc).ToArray();
        if (register >= locals.Length) return "register:" + register;
        var name = locals[register].Name;
        if (locals.Count(l => l.Name == name) > 1) return "local:" + Compact(name) + ":shadow:" + register;
        return "local:" + Compact(name);
    }
    private static string[] ChildCaptures(LuaPrototype parent, LuaPrototype child, string[] parentCaptures, int pc) =>
        child.Upvalues.Select(u => u.InStack == 1 ? RegisterName(parent, u.Index, pc) : parentCaptures[u.Index]).ToArray();

    private LuaDisassembly[] Render(LuaPrototype p, string[] captures, string[] constants, string[] children)
    {
        var result = new LuaDisassembly[p.Code.Length];
        for (var pc = 0; pc < result.Length; pc++)
        {
            var i = LuaOpcodes.Decode(p.Code[pc]); var op = LuaOpcodes.Get(_dialect, i.Opcode);
            string RK(int v) => v >= 256 ? constants[v & 255] : "R" + v;
            string Arg(int v, string mode) => mode == "K" ? RK(v) : mode == "R" ? "R" + v : v.ToString(CultureInfo.InvariantCulture);
            var a = op.Name == "SETTABUP" ? "UP(" + captures[i.A] + ")" : i.A.ToString(CultureInfo.InvariantCulture);
            var args = new List<string> { a }; int? target = null;
            switch (op.Mode)
            {
                case "ABx":
                    args.Add(op.Name switch { "LOADK" => constants[i.Bx], "LOADKX" => constants[LuaOpcodes.Decode(p.Code[pc + 1]).Ax], "CLOSURE" => "PROTO(" + Compact(children[i.Bx]) + ")", _ => i.Bx.ToString() }); break;
                case "AsBx": target = pc + 2 + i.SBx; args.Add("<target>"); break;
                case "Ax":
                    args.Clear();
                    args.Add(pc > 0 && LuaOpcodes.Get(_dialect, LuaOpcodes.Decode(p.Code[pc - 1]).Opcode).Name == "LOADKX" ? constants[i.Ax] : i.Ax.ToString()); break;
                default:
                    if (op.Name is "GETUPVAL" or "GETTABUP") args.Add("UP(" + captures[i.B] + ")");
                    else if (op.Name == "SETUPVAL") args.Add("UP(" + captures[i.B] + ")");
                    else if (op.BMode != "N") args.Add(Arg(i.B, op.BMode));
                    if (op.CMode != "N") args.Add(Arg(i.C, op.CMode));
                    if (op.Name is "EQ" or "LT" or "LE" or "TEST" or "TESTSET" || op.Name == "LOADBOOL" && i.C != 0) target = pc + 3;
                    break;
            }
            var normalized = op.Name + " " + string.Join(" ", args);
            _characters += normalized.Length;
            if (_characters > 16 * 1024 * 1024) throw new AnalysisException("LuaResourceLimit", "Normalized instruction text exceeds limit.");
            result[pc] = new(pc + 1, p.Lines.Length > pc ? p.Lines[pc] : null, op.Name, args.ToArray(), normalized, target, normalized.Contains("...#sha256:", StringComparison.Ordinal));
        }
        return result;
    }

    private static string Escape(string s) => s.Replace("~", "~0").Replace("/", "~1");
    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    private static string Compact(string s) => s.Length <= 256 ? s : s[..96] + "...#sha256:" + Hash(s);
    private static string Constant(LuaConstant c) => c.Value switch
    {
        null => "nil",
        string s => "string:" + JsonConvert.SerializeObject(Compact(s)),
        byte[] b => "bytes:" + Convert.ToHexString(SHA256.HashData(b)) + ":length:" + b.Length,
        bool b => b ? "true" : "false",
        double d => "float:" + d.ToString("R", CultureInfo.InvariantCulture) + ":bits:" + BitConverter.DoubleToInt64Bits(d).ToString("X16"),
        _ => "integer:" + Convert.ToString(c.Value, CultureInfo.InvariantCulture)
    };
}
