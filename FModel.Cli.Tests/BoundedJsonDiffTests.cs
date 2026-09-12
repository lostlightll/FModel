using FModel.Cli;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public sealed class BoundedJsonDiffTests
{
    private static JsonDiffResult Diff(string before, string after, int limit = 100, int depth = 20, int nodes = 1000)
        => BoundedJsonDiff.Compare(JToken.Parse(before), JToken.Parse(after), limit, depth, nodes);

    [Fact]
    public void ReportsOnlyChangedFields()
    {
        var result = Diff("{a:1,b:{c:2},d:[1,2]}", "{a:1,b:{c:3},d:[1,2]}");
        var difference = Assert.Single(result.Differences);
        Assert.Equal("/b/c", difference.Path);
        Assert.Equal(2L, difference.Before);
        Assert.Equal(3L, difference.After);
        Assert.False(result.Truncated);
        Assert.Equal(1, result.DifferenceCountLowerBound);
    }

    [Fact]
    public void ExpandsAddedAndRemovedObjectsAndEscapesPointers()
    {
        var result = Diff("{'gone':{'a/b~c':7}}", "{'new':{'a/b~c':[8]}}");
        Assert.Collection(result.Differences,
            d => { Assert.Equal("/gone/a~1b~0c", d.Path); Assert.Equal("removed", d.Kind); },
            d => { Assert.Equal("/new/a~1b~0c/0", d.Path); Assert.Equal("added", d.Kind); });
        Assert.DoesNotContain(result.Differences, d => d.Before is JObject || d.After is JObject);
    }

    [Fact]
    public void DifferenceLimitRetainsEvidenceOfAdditionalDifferences()
    {
        var result = Diff("[1,2,3]", "[4,5,6]", limit: 1);
        Assert.Single(result.Differences);
        Assert.Equal(2, result.DifferenceCountLowerBound);
        Assert.Contains("maxDifferences", result.Reasons);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void ExactDifferenceLimitDoesNotFalselyClaimTruncation()
    {
        var result = Diff("[1,2]", "[3,2]", limit: 1);
        Assert.False(result.Truncated);
    }

    [Theory]
    [InlineData(0, 100, "maxDepth")]
    [InlineData(10, 1, "maxNodes")]
    public void IncompleteTraversalCannotClaimEquality(int depth, int nodes, string reason)
    {
        var result = Diff("{a:{b:1}}", "{a:{b:2}}", depth: depth, nodes: nodes);
        Assert.True(result.Truncated);
        Assert.Contains(reason, result.Reasons);
        Assert.Empty(result.Differences);
        Assert.InRange(result.VisitedNodes, 1, nodes);
    }

    [Fact]
    public void BoundsScanningOfIdenticalWideObjects()
    {
        var obj = new JObject(Enumerable.Range(0, 10000).Select(i => new JProperty(i.ToString(), i)));
        var result = BoundedJsonDiff.Compare(obj, obj, 10, 10, 20);
        Assert.Equal(20, result.VisitedNodes);
        Assert.Contains("maxNodes", result.Reasons);
    }

    [Fact]
    public void EmptyCollectionsAndTypeReplacementsRemainVisible()
    {
        var emptyChange = Diff("{}", "[]");
        Assert.Single(emptyChange.Differences);
        var replacement = Diff("{a:{b:1}}", "{a:2}");
        Assert.Collection(replacement.Differences,
            d => { Assert.Equal("/a/b", d.Path); Assert.Equal("removed", d.Kind); },
            d => { Assert.Equal("/a", d.Path); Assert.Equal("added", d.Kind); });
    }

    [Fact]
    public void LongScalarValuesAreBoundedAndMarked()
    {
        var result = BoundedJsonDiff.Compare(new JValue("old"), new JValue(new string('x', 10000)), 10, 10, 10);
        Assert.Contains("maxValueChars", result.Reasons);
        Assert.True(JsonConvert.SerializeObject(result).Length < 3000);
    }
}
