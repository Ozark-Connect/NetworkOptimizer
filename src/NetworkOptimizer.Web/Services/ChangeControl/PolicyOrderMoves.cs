namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>A row next to a moved policy: its 1-based position, or an end-of-list marker.</summary>
public sealed record OrderNeighbor(int Position, string Name, bool IsSubject = false, bool IsEnd = false);

/// <summary>One policy that moved within a zone pair's order, with its neighbors before and after.</summary>
public sealed record OrderMove(string Name, int From, int To, IReadOnlyList<OrderNeighbor> Before, IReadOnlyList<OrderNeighbor> After);

/// <summary>
/// Works out which policies moved between two orders of one zone pair. The moved set is the smallest
/// one that explains the new order: every policy outside the longest run kept in order, so dragging
/// one rule reads as one move rather than every rule it shifted.
/// </summary>
public static class PolicyOrderMoves
{
    /// <summary>Neighbors shown each side of a moved policy.</summary>
    public const int Context = 2;

    /// <summary>Moves listed before the rest collapse into a count.</summary>
    public const int MaxShown = 5;

    public static List<OrderMove> Compute(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var (keptBefore, keptAfter) = LongestKeptRun(before, after);

        // A policy only in the new order was created, not moved; one only in the old order was deleted.
        var unmatchedBefore = Enumerable.Range(0, before.Count).Where(i => !keptBefore.Contains(i)).ToList();
        var moves = new List<OrderMove>();
        for (var to = 0; to < after.Count; to++)
        {
            if (keptAfter.Contains(to)) continue;
            var from = unmatchedBefore.FirstOrDefault(i => before[i] == after[to], -1);
            if (from < 0) continue;
            unmatchedBefore.Remove(from);
            moves.Add(new OrderMove(after[to], from + 1, to + 1, Window(before, from), Window(after, to)));
        }
        return moves;
    }

    /// <summary>Indexes in each list of the longest common subsequence.</summary>
    private static (HashSet<int> Before, HashSet<int> After) LongestKeptRun(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var len = new int[a.Count + 1, b.Count + 1];
        for (var i = a.Count - 1; i >= 0; i--)
            for (var j = b.Count - 1; j >= 0; j--)
                len[i, j] = a[i] == b[j] ? len[i + 1, j + 1] + 1 : Math.Max(len[i + 1, j], len[i, j + 1]);

        var keptA = new HashSet<int>();
        var keptB = new HashSet<int>();
        for (int i = 0, j = 0; i < a.Count && j < b.Count;)
        {
            if (a[i] == b[j]) { keptA.Add(i++); keptB.Add(j++); }
            else if (len[i + 1, j] >= len[i, j + 1]) i++;
            else j++;
        }
        return (keptA, keptB);
    }

    private static List<OrderNeighbor> Window(IReadOnlyList<string> list, int at)
    {
        var rows = new List<OrderNeighbor>();
        for (var i = Math.Max(0, at - Context); i <= Math.Min(list.Count - 1, at + Context); i++)
            rows.Add(new OrderNeighbor(i + 1, list[i], IsSubject: i == at));
        if (at == list.Count - 1) rows.Add(new OrderNeighbor(0, "end of list", IsEnd: true));
        return rows;
    }
}
