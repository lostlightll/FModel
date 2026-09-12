using FModel.Cli.Lua;

public sealed class LuaSequenceDiffTests
{
    private static LuaSequenceDiffResult Diff(string[] before, string[] after, int maxChanges = 100, int maxWork = 100000)
        => LuaSequenceDiff.Compare(before, after, maxChanges, maxWork);

    [Fact]
    public void InsertionDoesNotCascade()
    {
        var result = Diff(["load", "call", "return"], ["load", "new", "call", "return"]);
        var change = Assert.Single(result.Changes);
        Assert.Equal(new LuaSequenceChange("insert", 1, 1, null, "new"), change);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void RepeatedInstructionsAreAligned()
    {
        var result = Diff(["x", "a", "x", "b", "x"], ["x", "b", "x", "a", "x"]);
        Assert.Equal(4, result.Changes.Count);
        Assert.False(result.Truncated);
        AssertApplies(["x", "a", "x", "b", "x"], ["x", "b", "x", "a", "x"], result);
    }

    [Fact]
    public void ReplacementIsDeleteAndInsert()
    {
        var result = Diff(["old"], ["new"]);
        Assert.Equal(["delete", "insert"], result.Changes.Select(c => c.Kind));
        Assert.False(result.Truncated);
    }

    [Fact]
    public void EmptyAndEqualInputsProduceNoEdits()
    {
        Assert.Empty(Diff([], []).Changes);
        var equal = Diff(["a", "b"], ["a", "b"]);
        Assert.Empty(equal.Changes);
        Assert.False(equal.Truncated);
        Assert.Equal(2, equal.WorkUsed);
        Assert.Equal(2, Diff([], ["a", "b"]).Changes.Count);
        Assert.Equal(2, Diff(["a", "b"], []).Changes.Count);
    }

    [Fact]
    public void TinyBudgetBoundsHugeMismatch()
    {
        var result = Diff(Enumerable.Repeat("a", 100000).ToArray(), Enumerable.Repeat("b", 100000).ToArray(), maxWork: 4);
        Assert.True(result.Truncated);
        Assert.Contains("maxWork", result.Reasons);
        Assert.InRange(result.WorkUsed, 0, 4);
    }

    [Fact]
    public void TinyBudgetDoesNotClaimEquality()
    {
        var result = Diff(["a", "a", "a"], ["a", "a", "a"], maxWork: 1);
        Assert.True(result.Truncated);
        Assert.Empty(result.Changes);
        Assert.Equal(1, result.WorkUsed);
    }

    [Fact]
    public void ChangeLimitIsExactAndTruthful()
    {
        var exact = Diff([], ["a"], maxChanges: 1);
        Assert.False(exact.Truncated);
        var exceeded = Diff([], ["a", "b"], maxChanges: 1);
        Assert.True(exceeded.Truncated);
        Assert.Single(exceeded.Changes);
        Assert.Contains("maxChanges", exceeded.Reasons);
    }

    [Fact]
    public void RandomSmallSequencesHaveOptimalEditCountsAndCorrectAnchors()
    {
        var random = new Random(183);
        for (var trial = 0; trial < 300; trial++)
        {
            var before = Enumerable.Range(0, random.Next(15)).Select(_ => random.Next(4).ToString()).ToArray();
            var after = Enumerable.Range(0, random.Next(15)).Select(_ => random.Next(4).ToString()).ToArray();
            var result = Diff(before, after);
            Assert.False(result.Truncated);
            AssertApplies(before, after, result);
            var lcs = new int[before.Length + 1, after.Length + 1];
            for (var b = 1; b <= before.Length; b++)
                for (var a = 1; a <= after.Length; a++)
                    lcs[b, a] = before[b - 1] == after[a - 1]
                        ? lcs[b - 1, a - 1] + 1 : Math.Max(lcs[b - 1, a], lcs[b, a - 1]);
            Assert.Equal(before.Length + after.Length - 2 * lcs[before.Length, after.Length], result.Changes.Count);
        }
    }

    private static void AssertApplies(string[] before, string[] after, LuaSequenceDiffResult result)
    {
        var output = new List<string>();
        var cursor = 0;
        foreach (var change in result.Changes)
        {
            while (cursor < change.BeforeIndex) output.Add(before[cursor++]);
            Assert.Equal(output.Count, change.AfterIndex);
            if (change.Kind == "delete") { Assert.Equal(before[cursor], change.Before); cursor++; }
            else output.Add(change.After!);
        }
        while (cursor < before.Length) output.Add(before[cursor++]);
        Assert.Equal(after, output);
    }
}
