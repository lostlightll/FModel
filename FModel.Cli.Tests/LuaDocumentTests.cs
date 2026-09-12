using FModel.Cli;
using FModel.Cli.Lua;
using Newtonsoft.Json;

public sealed class LuaDocumentTests
{
    private static int Op(string name) => Enumerable.Range(0, 47).Single(i => LuaOpcodes.Get("lua53", i).Name == name);
    private static uint Abc(string name, int a = 0, int b = 0, int c = 0) => (uint)(Op(name) | a << 6 | b << 23 | c << 14);
    private static uint Abx(string name, int a, int bx) => (uint)(Op(name) | a << 6 | bx << 14);
    private static uint Jump(int offset) => Abx("JMP", 0, offset + 131071);
    private static uint Ret => Abc("RETURN", b: 1);
    private static LuaConstant K(string value) => new(4, value);
    private static LuaPrototype Body(uint[]? code = null, LuaConstant[]? constants = null,
        LuaUpvalue[]? upvalues = null, int parameters = 0, int vararg = 0) => new()
        { Code = code ?? [Ret], Constants = constants ?? [], Upvalues = upvalues ?? [], MaxStack = 5, Parameters = parameters, Vararg = vararg };
    private static LuaDocument Doc(LuaPrototype main) => new(new("lua53", main));
    private static LuaDiffResult Diff(LuaPrototype before, LuaPrototype after, int maxChanges = 1000, int maxWork = 100000)
        => LuaFunctionDiff.Compare(Doc(before), Doc(after), null, 0, 100, maxChanges, maxWork);
    private static LuaPrototype Methods((string Name, LuaPrototype Body)[] methods, bool reversePool = false)
    {
        var pool = methods.Select(m => m.Name).ToArray();
        if (reversePool) Array.Reverse(pool);
        var code = new List<uint>();
        for (var i = 0; i < methods.Length; i++)
        {
            code.Add(Abx("CLOSURE", 0, i));
            code.Add(Abc("SETTABUP", 0, 256 + Array.IndexOf(pool, methods[i].Name), 0));
        }
        code.Add(Ret);
        return new() { Code = code.ToArray(), Constants = pool.Select(K).ToArray(), Children = methods.Select(m => m.Body).ToArray(), Upvalues = [new(1, 0), new(1, 1)], MaxStack = 5 };
    }

    [Fact]
    public void PrependingMethodAndReorderingConstantsPreservesExistingFunctionIdentity()
    {
        var original = Body([Abx("LOADK", 0, 0), Ret], [K("value")]);
        var before = Methods([("first", original), ("second", Body())]);
        var after = Methods([("new", Body()), ("first", original), ("second", Body())], true);
        var oldDoc = Doc(before); var newDoc = Doc(after);
        var oldIds = oldDoc.Functions.Keys.Where(id => id != "$").ToArray();
        Assert.All(oldIds, id => Assert.True(newDoc.Functions.ContainsKey(id)));
        var diff = Diff(before, after);
        Assert.False(diff.Truncated);
        Assert.DoesNotContain(diff.ChangedFunctions, f => oldIds.Contains(f.Id));
        Assert.Single(diff.ChangedFunctions.Where(f => f.Kind == "added"));
    }

    [Fact]
    public void ConstantPoolReorderingPreservesInstructions()
    {
        var before = Body([Abx("LOADK", 0, 0), Abx("LOADK", 1, 1), Ret], [K("alpha"), K("beta")]);
        var after = Body([Abx("LOADK", 0, 1), Abx("LOADK", 1, 0), Ret], [K("beta"), K("alpha")]);
        Assert.Equal(0, Diff(before, after).TotalChangedFunctions);
    }

    [Fact]
    public void UpvalueSlotReorderingPreservesActualBindings()
    {
        var before = Body([Abc("GETUPVAL", 0, 0), Abc("GETUPVAL", 1, 1), Ret], upvalues: [new(0, 0), new(0, 1)]);
        var after = Body([Abc("GETUPVAL", 0, 1), Abc("GETUPVAL", 1, 0), Ret], upvalues: [new(0, 1), new(0, 0)]);
        var result = Diff(Methods([("method", before)]), Methods([("method", after)]));
        Assert.Equal(0, result.TotalChangedFunctions);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void ActualCaptureChangeIsReported()
    {
        var before = Body([Abc("GETUPVAL", 0, 0), Ret], upvalues: [new(0, 0)]);
        var after = Body([Abc("GETUPVAL", 0, 0), Ret], upvalues: [new(0, 1)]);
        var changed = Assert.Single(Diff(Methods([("method", before)]), Methods([("method", after)])).ChangedFunctions);
        Assert.Contains(changed.Changes, c => c.Field == "captures");
        Assert.Contains(changed.Changes, c => c.Before?.Opcode == "GETUPVAL");
    }

    [Fact]
    public void InsertionShiftsJumpOffsetWithoutFalseBranchDifference()
    {
        var before = Body([Jump(1), Abx("LOADK", 0, 0), Ret], [K("unchanged")]);
        var after = Body([Jump(2), Abc("LOADBOOL", 1, 1), Abx("LOADK", 0, 0), Ret], [K("unchanged")]);
        var changed = Assert.Single(Diff(before, after).ChangedFunctions);
        var instruction = Assert.Single(changed.Changes);
        Assert.Equal("insert", instruction.Kind);
        Assert.Equal("LOADBOOL", instruction.After!.Opcode);
    }

    [Fact]
    public void ChangedJumpDestinationIsReported()
    {
        var before = Body([Jump(0), Abx("LOADK", 0, 0), Ret], [K("value")]);
        var after = Body([Jump(1), Abx("LOADK", 0, 0), Ret], [K("value")]);
        var change = Assert.Single(Assert.Single(Diff(before, after).ChangedFunctions).Changes);
        Assert.Equal("branch-target", change.Kind);
        Assert.Equal(2, change.Before!.TargetPc);
        Assert.Equal(3, change.After!.TargetPc);
    }

    [Fact]
    public void NestedLocalAndCallbackIdentitiesUseBindings()
    {
        var nested = new LuaPrototype
        {
            Code = [Abx("CLOSURE", 0, 0), Ret], Children = [Body()],
            Locals = [new("helper", 1, 3)], MaxStack = 5
        };
        var root = new LuaPrototype
        {
            Code = [Abc("GETTABUP", 0, 0, 256), Abx("CLOSURE", 1, 0), Abc("CALL", 0, 2, 1), Ret],
            Constants = [K("register")], Upvalues = [new(1, 0)], Children = [nested], MaxStack = 5
        };
        var doc = Doc(root);
        Assert.Contains(doc.Functions.Values, f => f.Identity.StartsWith("callback:") && f.Identity.EndsWith("/argument:1"));
        Assert.Contains(doc.Functions.Values, f => f.Identity == "local:helper" && f.Id.Contains("callback:"));
        Assert.Equal(0, Diff(root, root).TotalChangedFunctions);
    }

    [Fact]
    public void SameNamedHandlersInDifferentlyKeyedTablesRemainDistinct()
    {
        var code = new List<uint>(); var children = new List<LuaPrototype>();
        var names = new[] { "first", "second", "handler", "finishhandler" };
        for (var table = 0; table < 2; table++)
        {
            code.Add(Abc("NEWTABLE", 0));
            for (var field = 2; field < 4; field++)
            {
                code.Add(Abx("CLOSURE", 1, children.Count)); children.Add(Body());
                code.Add(Abc("SETTABLE", 0, 256 + field, 1));
            }
            code.Add(Abc("SETTABUP", 0, 256 + table, 0));
        }
        code.Add(Ret);
        var doc = Doc(new() { Code = code.ToArray(), Constants = names.Select(K).ToArray(), Children = children.ToArray(), Upvalues = [new(1, 0)], MaxStack = 5 });
        Assert.Equal(5, doc.Functions.Count);
        Assert.Equal(2, doc.Functions.Values.Count(f => f.Identity.EndsWith("string:\"handler\"")));
        Assert.Equal(2, doc.Functions.Values.Count(f => f.Identity.EndsWith("string:\"finishhandler\"")));
    }

    [Fact]
    public void IndistinguishableClosuresWithoutDebugFailExplicitly()
    {
        var root = new LuaPrototype { Code = [Abx("CLOSURE", 0, 0), Abx("CLOSURE", 1, 1), Ret], Children = [Body(), Body()], MaxStack = 5 };
        var error = Assert.Throws<AnalysisException>(() => Doc(root));
        Assert.Contains("indistinguishable", error.Message);
    }

    [Fact]
    public void LongConstantsStayCompactButDifferentSuffixesRemainVisible()
    {
        var first = new string('x', 20000) + "a";
        var second = new string('x', 20000) + "b";
        var instructions = new[] { Abx("LOADK", 0, 0), Abx("LOADK", 1, 0), Ret };
        var before = Body(instructions, [K(first)]); var after = Body(instructions, [K(second)]);
        var doc = Doc(before);
        Assert.All(doc.Functions["$"].Instructions, i => Assert.True(i.Normalized.Length < 512));
        Assert.Equal(2, doc.Functions["$"].Instructions.Count(i => i.PreviewTruncated));
        var diff = Diff(before, after);
        Assert.Equal(4, Assert.Single(diff.ChangedFunctions).Changes.Count);
        Assert.True(JsonConvert.SerializeObject(diff).Length < 10000);
        Assert.False(diff.Truncated);
    }

    [Fact]
    public void ParametersAndVarargChangesAreReported()
    {
        var changed = Assert.Single(Diff(Body(), Body(parameters: 2, vararg: 1)).ChangedFunctions);
        Assert.Contains(changed.Changes, c => c.Field == "parameters" && Equals(c.AfterValue, 2));
        Assert.Contains(changed.Changes, c => c.Field == "vararg" && Equals(c.AfterValue, 1));
    }

    [Fact]
    public void RootConstantAndRegisterChangesAreVisible()
    {
        var before = Body([Abx("LOADK", 0, 0), Ret], [K("old")]);
        var after = Body([Abx("LOADK", 1, 0), Ret], [K("new")]);
        var changed = Assert.Single(Diff(before, after).ChangedFunctions);
        Assert.Equal("$", changed.Id);
        Assert.Equal(2, changed.Changes.Count);
        Assert.Contains(changed.Changes, c => c.After?.Normalized.Contains("LOADK 1 string:\"new\"") == true);
    }

    [Fact]
    public void ExhaustedWorkDoesNotInventProvenChangedFunction()
    {
        var result = Diff(Body(), Body(), maxWork: 1);
        Assert.True(result.Truncated);
        Assert.Null(result.TotalChangedFunctions);
        Assert.Equal(0, result.ChangedFunctionCountLowerBound);
        Assert.Contains("maxWork", result.Reasons);
        Assert.InRange(result.WorkUsed, 0, 1);
    }

    [Fact]
    public void ExhaustedChangeBudgetRetainsEvidenceAndSignalsIncomplete()
    {
        var result = Diff(Body(), Body(parameters: 2, vararg: 1), maxChanges: 1);
        Assert.True(result.Truncated);
        Assert.Null(result.TotalChangedFunctions);
        Assert.Equal(1, result.ChangedFunctionCountLowerBound);
        Assert.Contains("maxChanges", result.Reasons);
        Assert.Single(Assert.Single(result.ChangedFunctions).Changes);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(5)] [InlineData(10)] [InlineData(20)]
    public void WorkLimitAcrossEqualFunctionsNeverInflatesLowerBound(int budget)
    {
        var root = Methods([("first", Body()), ("second", Body())]);
        var result = Diff(root, root, maxWork: budget);
        Assert.InRange(result.WorkUsed, 0, budget);
        Assert.Equal(0, result.ChangedFunctionCountLowerBound);
        if (result.Truncated) Assert.Null(result.TotalChangedFunctions);
        else Assert.Equal(0, result.TotalChangedFunctions);
    }

    [Fact]
    public void FunctionFilterAndPaginationReturnOnlyRequestedChanges()
    {
        var before = Doc(Methods([("first", Body()), ("second", Body())]));
        var after = Doc(Methods([("first", Body(parameters: 1)), ("second", Body(parameters: 2))]));
        var all = LuaFunctionDiff.Compare(before, after, null, 0, 100, 100, 100000);
        Assert.Equal(2, all.TotalChangedFunctions);
        var requested = all.ChangedFunctions[1].Id;
        var filtered = LuaFunctionDiff.Compare(before, after, requested, 0, 1, 100, 100000);
        Assert.Equal(requested, Assert.Single(filtered.ChangedFunctions).Id);
        var page = LuaFunctionDiff.Compare(before, after, null, 1, 1, 100, 100000);
        Assert.Equal(requested, Assert.Single(page.ChangedFunctions).Id);
        Assert.Equal(2, page.TotalChangedFunctions);
        Assert.False(page.Truncated);
    }

    [Fact]
    public void RootOnlyAllowsUnrelatedAmbiguousClosuresAndDetectsPreciseOperandChange()
    {
        LuaPrototype Root(int value) => new()
        {
            Code = [Abx("CLOSURE", 0, 0), Abx("CLOSURE", 1, 1), Abc("LOADBOOL", 2, value), Ret],
            Children = [Body(), Body()], MaxStack = 5
        };
        Assert.Throws<AnalysisException>(() => Doc(Root(0)));
        var before = new LuaDocument(new("lua53", Root(0)), rootOnly: true);
        var after = new LuaDocument(new("lua53", Root(1)), rootOnly: true);
        Assert.Equal("$", Assert.Single(before.Functions).Key);
        Assert.Equal("$", Assert.Single(after.Functions).Key);
        var result = LuaFunctionDiff.Compare(before, after, "$", 0, 1, 10, 10000);
        var changed = Assert.Single(result.ChangedFunctions);
        Assert.False(result.Truncated);
        Assert.Equal(2, changed.Changes.Count);
        Assert.Contains(changed.Changes, c => c.Before?.Normalized == "LOADBOOL 2 0 0");
        Assert.Contains(changed.Changes, c => c.After?.Normalized == "LOADBOOL 2 1 0");
    }

    [Fact]
    public void RootOnlyDoesNotHideSwappedUnresolvedClosureOperands()
    {
        LuaPrototype Root(bool swapped) => new()
        {
            Code = [Abx("CLOSURE", 0, swapped ? 1 : 0), Abx("CLOSURE", 1, swapped ? 0 : 1), Ret],
            Children = [Body(), Body()], MaxStack = 5
        };
        var before = new LuaDocument(new("lua53", Root(false)), rootOnly: true);
        var after = new LuaDocument(new("lua53", Root(true)), rootOnly: true);
        Assert.True(before.HasUnresolvedChildIdentities);
        Assert.True(after.HasUnresolvedChildIdentities);
        Assert.Equal("$", Assert.Single(before.Functions).Key);
        Assert.Equal("$", Assert.Single(after.Functions).Key);
        Assert.Contains("unresolved-local-prototype:", before.Functions["$"].Instructions[0].Normalized);
        var result = LuaFunctionDiff.Compare(before, after, "$", 0, 1, 10, 10000);
        Assert.False(result.Truncated);
        Assert.Equal(1, result.TotalChangedFunctions);
        var changed = Assert.Single(result.ChangedFunctions);
        Assert.Equal("$", changed.Id);
        Assert.NotEmpty(changed.Changes);
        Assert.All(changed.Changes, c => Assert.Equal("CLOSURE", (c.Before ?? c.After)!.Opcode));
    }

    [Fact]
    public void StringConstantJustOverPreviewLimitMarksInstructionTruncated()
    {
        var constant = new string('q', 257);
        var doc = Doc(Body([Abx("LOADK", 0, 0), Ret], [K(constant)]));
        var load = doc.Functions["$"].Instructions[0];
        Assert.True(load.PreviewTruncated);
        Assert.Contains("...#sha256:", load.Normalized);
        Assert.DoesNotContain(constant, load.Normalized);
        Assert.False(doc.Functions["$"].Instructions[1].PreviewTruncated);
    }

    [Fact]
    public void OffsetSkipsLargeDiffWithoutConsumingOutputChangeBudget()
    {
        var before = Doc(Methods([("first", Body()), ("second", Body())]));
        var largeCode = Enumerable.Range(0, 30).Select(_ => Abc("LOADBOOL", 0, 1)).Append(Ret).ToArray();
        var after = Doc(Methods([("first", Body(largeCode)), ("second", Body(parameters: 1))]));
        var firstPage = LuaFunctionDiff.Compare(before, after, null, 0, 1, 1, 100000);
        Assert.True(firstPage.Truncated);
        Assert.Contains("maxChanges", firstPage.Reasons);
        var nextPage = LuaFunctionDiff.Compare(before, after, null, 1, 1, 1, 100000);
        Assert.False(nextPage.Truncated);
        Assert.Equal(2, nextPage.TotalChangedFunctions);
        var method = Assert.Single(nextPage.ChangedFunctions);
        Assert.Contains("second", method.Id);
        Assert.Equal("parameters", Assert.Single(method.Changes).Field);
    }

    [Theory]
    [InlineData("LOADNIL", 1)]
    [InlineData("VARARG", 3)]
    [InlineData("VARARG", 0)]
    public void MultiRegisterWritesDiscardOverwrittenClosureBindings(string opcode, int b)
    {
        var root = new LuaPrototype
        {
            Code = [Abx("CLOSURE", 2, 0), Abc(opcode, 1, b), Abc("SETTABUP", 0, 256, 2), Ret],
            Children = [Body()], Constants = [K("incorrectName")], Upvalues = [new(1, 0)], MaxStack = 5, Vararg = 1
        };
        var child = Assert.Single(Doc(root).Functions.Values.Where(f => f.Id != "$"));
        Assert.StartsWith("structural:", child.Identity);
        Assert.DoesNotContain("incorrectName", child.Identity);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SkippedClosureCannotCreateFalseNamedIdentity(bool useJump)
    {
        var root = new LuaPrototype
        {
            Code = [useJump ? Jump(1) : Abc("LOADBOOL", 0, 1, 1), Abx("CLOSURE", 1, 0), Abc("SETTABUP", 0, 256, 1), Ret],
            Children = [Body()], Constants = [K("incorrectName")], Upvalues = [new(1, 0)], MaxStack = 5
        };
        var child = Assert.Single(Doc(root).Functions.Values.Where(f => f.Id != "$"));
        Assert.StartsWith("structural:", child.Identity);
        Assert.DoesNotContain("incorrectName", child.Identity);
    }
}
