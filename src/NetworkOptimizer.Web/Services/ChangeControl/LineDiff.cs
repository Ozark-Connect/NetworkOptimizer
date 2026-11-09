namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>A unified line diff (LCS) for the Compare YAML view, with context lines around each change.</summary>
public static class LineDiff
{
    /// <summary>Lines marked ' ', '-', or '+'; unchanged runs beyond the context collapse to "...".</summary>
    public static List<(char Kind, string Text)> Diff(IReadOnlyList<string> a, IReadOnlyList<string> b, int context = 3)
    {
        var n = a.Count;
        var m = b.Count;
        // Small files only (one or two collections), so the O(n*m) table is fine; cap it regardless.
        if ((long)n * m > 4_000_000)
            return [.. a.Select(l => ('-', l)), .. b.Select(l => ('+', l))];

        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var all = new List<(char, string)>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y]) { all.Add((' ', a[x])); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) { all.Add(('-', a[x])); x++; }
            else { all.Add(('+', b[y])); y++; }
        }
        while (x < n) all.Add(('-', a[x++]));
        while (y < m) all.Add(('+', b[y++]));

        var keep = new bool[all.Count];
        for (var i = 0; i < all.Count; i++)
            if (all[i].Item1 != ' ')
                for (var k = Math.Max(0, i - context); k <= Math.Min(all.Count - 1, i + context); k++)
                    keep[k] = true;

        var result = new List<(char, string)>();
        var skipped = false;
        for (var i = 0; i < all.Count; i++)
        {
            if (keep[i]) { result.Add(all[i]); skipped = false; }
            else if (!skipped) { result.Add((' ', "...")); skipped = true; }
        }
        return result;
    }
}
