using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace FModel.Cli.Lua;

[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
internal sealed record LuaMetadata(int Parameters, int Vararg, int MaxStack, string[] Captures);
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
internal sealed record LuaChange(string Kind, LuaDisassembly? Before, LuaDisassembly? After, string? Field = null, object? BeforeValue = null, object? AfterValue = null);
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
internal sealed record LuaChangedFunction(string Id, string Kind, IReadOnlyList<LuaChange> Changes, bool Truncated, IReadOnlyList<string> Reasons);
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
internal sealed record LuaDiffResult(int? TotalChangedFunctions, int ChangedFunctionCountLowerBound, int Offset, int Limit,
    IReadOnlyList<LuaChangedFunction> ChangedFunctions, bool Truncated, IReadOnlyList<string> Reasons, int WorkUsed);

internal static class LuaFunctionDiff
{
    internal static LuaMetadata Metadata(LuaFunction f) => new(f.Prototype.Parameters, f.Prototype.Vararg,
        f.Prototype.MaxStack, f.Captures.Order(StringComparer.Ordinal).ToArray());

    public static LuaDiffResult Compare(LuaDocument before, LuaDocument after, string? function, int offset, int limit, int maxChanges, int maxWork)
    {
        var ids = before.Functions.Keys.Union(after.Functions.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (function is not null)
        {
            if (!ids.Contains(function, StringComparer.Ordinal)) throw new AnalysisException("LuaFunctionNotFound", "The exact function id was not found in either version.");
            ids = [function];
        }
        var output = new List<LuaChangedFunction>(); var reasons = new HashSet<string>();
        var count = 0; var work = 0; var emitted = 0;
        foreach (var id in ids)
        {
            if (work >= maxWork) { reasons.Add("maxWork"); break; }
            work++;
            before.Functions.TryGetValue(id, out var old); after.Functions.TryGetValue(id, out var current);
            var changes = new List<LuaChange>(); var localReasons = new HashSet<string>();
            var oldCode = old?.Instructions ?? []; var newCode = current?.Instructions ?? [];
            // Off-page functions still need a truthful changed/unchanged classification, but no output payload.
            var onPage = count >= offset && output.Count < limit;
            if (!onPage)
            {
                // Classification outside the requested page needs no edit script or output-change budget.
                var differs = old is null || current is null;
                if (!differs)
                {
                    var x = Metadata(old!); var y = Metadata(current!);
                    differs = x.Parameters != y.Parameters || x.Vararg != y.Vararg || x.MaxStack != y.MaxStack ||
                        !x.Captures.SequenceEqual(y.Captures) || oldCode.Length != newCode.Length;
                }
                var known = true;
                for (var i = 0; !differs && i < oldCode.Length; i++)
                {
                    if (work >= maxWork) { known = false; reasons.Add("maxWork"); break; }
                    work++;
                    differs = oldCode[i].Normalized != newCode[i].Normalized || oldCode[i].TargetPc != newCode[i].TargetPc;
                }
                if (differs) count++;
                if (!known) break;
                continue;
            }
            var available = onPage ? maxChanges - emitted : maxChanges;
            var different = false;
            void Add(LuaChange c)
            {
                different = true;
                if (changes.Count < available) changes.Add(c); else localReasons.Add("maxChanges");
            }
            if (old is null || current is null)
                Add(new("metadata", null, null, "function", old is null ? null : Metadata(old), current is null ? null : Metadata(current)));
            else
            {
                var x = Metadata(old); var y = Metadata(current);
                if (x.Parameters != y.Parameters) Add(new("metadata", null, null, "parameters", x.Parameters, y.Parameters));
                if (x.Vararg != y.Vararg) Add(new("metadata", null, null, "vararg", x.Vararg, y.Vararg));
                if (x.MaxStack != y.MaxStack) Add(new("metadata", null, null, "maxStack", x.MaxStack, y.MaxStack));
                if (!x.Captures.SequenceEqual(y.Captures)) Add(new("metadata", null, null, "captures", x.Captures, y.Captures));
            }
            if (work >= maxWork) localReasons.Add("maxWork");
            else
            {
                var aligned = LuaSequenceDiff.Compare(oldCode.Select(i => i.Normalized).ToArray(), newCode.Select(i => i.Normalized).ToArray(),
                    Math.Max(1, available - changes.Count), maxWork - work);
                work += aligned.WorkUsed;
                foreach (var c in aligned.Changes)
                    Add(new(c.Kind, c.Before is null ? null : oldCode[c.BeforeIndex], c.After is null ? null : newCode[c.AfterIndex]));
                foreach (var reason in aligned.Reasons) localReasons.Add(reason);
                if (!aligned.Truncated)
                {
                    // Recover paired PCs from the complete alignment. Branch offsets themselves are not semantic identities.
                    var removed = aligned.Changes.Where(c => c.Kind == "delete").Select(c => c.BeforeIndex).ToHashSet();
                    var inserted = aligned.Changes.Where(c => c.Kind == "insert").Select(c => c.AfterIndex).ToHashSet();
                    var pairs = new Dictionary<int, int>(); var b = 0; var a = 0;
                    while (b < oldCode.Length && a < newCode.Length)
                    {
                        if (work >= maxWork) { localReasons.Add("maxWork"); break; }
                        work++;
                        if (removed.Contains(b)) { b++; continue; }
                        if (inserted.Contains(a)) { a++; continue; }
                        pairs[a++] = b++;
                    }
                    foreach (var (afterPc, beforePc) in pairs)
                    {
                        if (work >= maxWork) { localReasons.Add("maxWork"); break; }
                        work++;
                        var left = oldCode[beforePc]; var right = newCode[afterPc];
                        if (left.TargetPc is not null || right.TargetPc is not null)
                        {
                            var targetMatches = right.TargetPc is { } r && left.TargetPc is { } l &&
                                pairs.TryGetValue(r - 1, out var targetBefore) && targetBefore == l - 1;
                            if (!targetMatches) Add(new("branch-target", left, right));
                        }
                    }
                }
            }
            if (changes.Count > 0 || localReasons.Count > 0)
            {
                if (different) count++;
                if (onPage)
                {
                    output.Add(new(id, !different ? "incomplete" : old is null ? "added" : current is null ? "removed" : "changed", changes, localReasons.Count > 0, localReasons.Order().ToArray()));
                    emitted += changes.Count;
                }
            }
            foreach (var reason in localReasons) reasons.Add(reason);
            if (localReasons.Count > 0) break;
        }
        return new(reasons.Count == 0 ? count : null, count, offset, limit, output, reasons.Count > 0, reasons.Order().ToArray(), work);
    }
}
