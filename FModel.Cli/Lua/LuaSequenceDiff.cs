namespace FModel.Cli.Lua;

internal sealed record LuaSequenceChange(string Kind, int BeforeIndex, int AfterIndex, string? Before, string? After);

internal sealed record LuaSequenceDiffResult(
    IReadOnlyList<LuaSequenceChange> Changes, bool Truncated, IReadOnlyList<string> Reasons, int WorkUsed);

/// <summary>
/// Bounded Hirschberg LCS alignment: linear row storage, no quadratic edit matrix.
/// Work includes comparisons, DP cells, split searches and emitted edits. Exhaustion
/// stops immediately; a partial result must never be interpreted as equality.
/// </summary>
internal static class LuaSequenceDiff
{
    internal static LuaSequenceDiffResult Compare(
        IReadOnlyList<string> before, IReadOnlyList<string> after, int maxChanges, int maxWork)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChanges, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWork, 1);
        var state = new Alignment(before, after, maxChanges, maxWork);
        return state.Run();
    }

    private sealed class LimitReached(string reason) : Exception
    {
        internal string Reason { get; } = reason;
    }

    private sealed class Alignment(IReadOnlyList<string> before, IReadOnlyList<string> after,
        int maxChanges, int maxWork)
    {
        private readonly List<LuaSequenceChange> _changes = [];
        private int _work;

        internal LuaSequenceDiffResult Run()
        {
            try
            {
                var start = 0;
                var beforeEnd = before.Count;
                var afterEnd = after.Count;
                while (start < beforeEnd && start < afterEnd && Equal(start, start)) start++;
                while (start < beforeEnd && start < afterEnd && Equal(beforeEnd - 1, afterEnd - 1))
                {
                    beforeEnd--;
                    afterEnd--;
                }
                Align(start, beforeEnd, start, afterEnd);
                return new(_changes, false, [], _work);
            }
            catch (LimitReached limit)
            {
                return new(_changes, true, [limit.Reason], _work);
            }
        }

        private void Tick()
        {
            if (_work == maxWork) throw new LimitReached("maxWork");
            _work++;
        }

        private bool Equal(int b, int a)
        {
            Tick();
            return string.Equals(before[b], after[a], StringComparison.Ordinal);
        }

        private void Add(bool insert, int b, int a)
        {
            // The limit is reported only upon finding one more actual edit.
            if (_changes.Count == maxChanges) throw new LimitReached("maxChanges");
            Tick();
            _changes.Add(new(insert ? "insert" : "delete", b, a,
                insert ? null : before[b], insert ? after[a] : null));
        }

        private void Align(int bs, int be, int @as, int ae)
        {
            if (bs == be)
            {
                for (var a = @as; a < ae; a++) Add(true, bs, a);
                return;
            }
            if (@as == ae)
            {
                for (var b = bs; b < be; b++) Add(false, b, @as);
                return;
            }
            if (be - bs == 1)
            {
                var match = -1;
                for (var a = @as; a < ae; a++)
                    if (Equal(bs, a)) { match = a; break; }
                if (match == -1)
                {
                    Add(false, bs, @as);
                    for (var a = @as; a < ae; a++) Add(true, be, a);
                }
                else
                {
                    for (var a = @as; a < match; a++) Add(true, bs, a);
                    for (var a = match + 1; a < ae; a++) Add(true, be, a);
                }
                return;
            }
            var middle = bs + (be - bs) / 2;
            // Row arrays live only in FindSplit, never on recursive stack frames.
            var split = FindSplit(bs, middle, be, @as, ae);
            Align(bs, middle, @as, split);
            Align(middle, be, split, ae);
        }

        private int FindSplit(int bs, int middle, int be, int @as, int ae)
        {
            var width = ae - @as;
            // Each row needs at least width comparisons. Reject before allocating
            // arrays when even one row cannot fit the remaining work allowance.
            if (width > maxWork - _work) throw new LimitReached("maxWork");
            var left = Scores(bs, middle, @as, ae, false);
            var right = Scores(middle, be, @as, ae, true);
            var best = -1;
            var split = 0;
            for (var i = 0; i <= width; i++)
            {
                Tick();
                var score = left[i] + right[width - i];
                if (score > best) { best = score; split = i; }
            }
            return @as + split;
        }

        private int[] Scores(int bs, int be, int @as, int ae, bool reverse)
        {
            var width = ae - @as;
            var row = new int[checked(width + 1)];
            for (var b = 0; b < be - bs; b++)
            {
                var diagonal = 0;
                for (var a = 1; a <= width; a++)
                {
                    var prior = row[a];
                    var equal = Equal(reverse ? be - b - 1 : bs + b,
                        reverse ? ae - a : @as + a - 1);
                    row[a] = equal ? diagonal + 1 : Math.Max(row[a], row[a - 1]);
                    diagonal = prior;
                }
            }
            return row;
        }
    }
}
