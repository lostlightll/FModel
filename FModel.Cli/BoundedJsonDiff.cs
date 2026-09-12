using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FModel.Cli;

public sealed record JsonFieldDifference(
    [property: JsonProperty("path")] string Path,
    [property: JsonProperty("kind")] string Kind,
    [property: JsonProperty("before")] object? Before,
    [property: JsonProperty("after")] object? After);

public sealed record JsonDiffResult(
    [property: JsonProperty("differences")] IReadOnlyList<JsonFieldDifference> Differences,
    [property: JsonProperty("truncated")] bool Truncated,
    [property: JsonProperty("reasons")] IReadOnlyList<string> Reasons,
    [property: JsonProperty("visitedNodes")] int VisitedNodes,
    [property: JsonProperty("differenceCountLowerBound")] int DifferenceCountLowerBound);

/// <summary>Streams a depth-first field diff without copying subtrees or comparing them eagerly.</summary>
public static class BoundedJsonDiff
{
    private const int MaxValueChars = 2048;
    private sealed record Pair(JToken? Before, JToken? After, string Path, int Depth, bool ScanOnly = false);

    public static JsonDiffResult Compare(JToken before, JToken after, int maxDifferences, int maxDepth, int maxNodes)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDifferences, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxNodes, 1);
        var differences = new List<JsonFieldDifference>();
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<IEnumerator<Pair>>();
        stack.Push(new[] { new Pair(before, after, "", 0) }.AsEnumerable().GetEnumerator());
        var visited = 0;
        var count = 0;
        try
        {
            while (stack.Count > 0)
            {
                var iterator = stack.Peek();
                if (!iterator.MoveNext()) { stack.Pop().Dispose(); continue; }
                if (visited == maxNodes) { reasons.Add("maxNodes"); break; }
                visited++;
                var pair = iterator.Current;
                if (pair.ScanOnly) continue;
                if (pair.Depth > maxDepth) { reasons.Add("maxDepth"); continue; }
                var left = pair.Before;
                var right = pair.After;
                if (left is JValue leftValue && right is JValue rightValue && leftValue.Equals(rightValue)) continue;
                if (IsBranch(left) || IsBranch(right))
                {
                    // Empty collections are leaves. Nonempty collection additions/removals are expanded.
                    stack.Push(Children(pair).GetEnumerator());
                    continue;
                }
                if (left is JContainer && right is JContainer && left.Type == right.Type) continue;
                count++;
                if (differences.Count == maxDifferences) { reasons.Add("maxDifferences"); break; }
                differences.Add(new(pair.Path, left is null ? "added" : right is null ? "removed" : "changed",
                    Display(left, reasons), Display(right, reasons)));
            }
        }
        finally { while (stack.Count > 0) stack.Pop().Dispose(); }
        return new(differences, reasons.Count > 0, reasons.Order(StringComparer.Ordinal).ToArray(), visited, count);
    }

    private static bool IsBranch(JToken? value) => value is JObject or JArray && value.HasValues;
    private static string Escape(string key) => key.Replace("~", "~0").Replace("/", "~1");

    private static IEnumerable<Pair> Children(Pair pair)
    {
        if (pair.Before is JObject beforeObject && pair.After is JObject afterObject)
        {
            foreach (var property in beforeObject.Properties())
                yield return new(property.Value, afterObject.Property(property.Name, StringComparison.Ordinal)?.Value,
                    pair.Path + "/" + Escape(property.Name), pair.Depth + 1);
            foreach (var property in afterObject.Properties())
            {
                // Count even duplicate-key scans against the work budget.
                var exists = beforeObject.Property(property.Name, StringComparison.Ordinal) is not null;
                yield return new(null, property.Value, pair.Path + "/" + Escape(property.Name), pair.Depth + 1, exists);
            }
        }
        else if (pair.Before is JArray beforeArray && pair.After is JArray afterArray)
        {
            for (var i = 0; i < Math.Max(beforeArray.Count, afterArray.Count); i++)
                yield return new(i < beforeArray.Count ? beforeArray[i] : null, i < afterArray.Count ? afterArray[i] : null,
                    pair.Path + "/" + i, pair.Depth + 1);
        }
        else
        {
            // A type replacement is represented as removals followed by additions, retaining leaf detail.
            if (pair.Before is not null)
                foreach (var child in OneSide(pair.Before, pair.Path, pair.Depth, true)) yield return child;
            if (pair.After is not null)
                foreach (var child in OneSide(pair.After, pair.Path, pair.Depth, false)) yield return child;
        }
    }

    private static IEnumerable<Pair> OneSide(JToken token, string path, int depth, bool before)
    {
        if (!IsBranch(token)) { yield return new(before ? token : null, before ? null : token, path, depth); yield break; }
        if (token is JObject obj)
        {
            foreach (var property in obj.Properties())
                yield return new(before ? property.Value : null, before ? null : property.Value, path + "/" + Escape(property.Name), depth + 1);
        }
        else if (token is JArray array)
        {
            for (var i = 0; i < array.Count; i++)
                yield return new(before ? array[i] : null, before ? null : array[i], path + "/" + i, depth + 1);
        }
    }

    private static object? Display(JToken? token, ISet<string> reasons)
    {
        if (token is null) return null;
        if (token is JContainer) return new { type = token.Type.ToString(), empty = true };
        var value = ((JValue)token).Value;
        if (value is byte[] bytes)
        {
            reasons.Add("binaryValueOmitted");
            return new { type = "Bytes", length = bytes.Length };
        }
        if (value is string text && text.Length > MaxValueChars)
        {
            reasons.Add("maxValueChars");
            return new { type = "String", preview = text[..MaxValueChars], length = text.Length, truncated = true };
        }
        return value;
    }
}
