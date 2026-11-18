namespace NetworkOptimizer.Web.Services.Monitoring.LiveTraffic;

/// <summary>
/// The rows of an open chip editor, in an order that never changes under the pointer. Set when the editor
/// opens: the chip's values first, then by count, then the muted sections (<see cref="LiveFacetValue.Rank"/>:
/// hidden by other filters, no traffic now, offline). Ticking a value or a refresh moving the counts leaves
/// every row where it is. A value seen later joins the end of its own section, and one that drops out of
/// the counts keeps its row.
/// </summary>
public sealed class FacetRowOrder
{
    private readonly List<string> _order = new();
    private readonly Dictionary<string, int> _rank = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every value so far, in row order, after placing any the chip or the counts add.</summary>
    public IReadOnlyList<string> Arrange(IEnumerable<string> selected, IEnumerable<LiveFacetValue> facets)
    {
        foreach (var v in selected.Order(StringComparer.OrdinalIgnoreCase)) Place(v, 0);
        foreach (var f in facets.OrderBy(f => f.Rank)) Place(f.Value, f.Rank);
        return _order;
    }

    /// <summary>The section a value was placed in, for one the counts no longer list.</summary>
    public int RankOf(string value) => _rank.GetValueOrDefault(value);

    private void Place(string value, int rank)
    {
        if (!_rank.TryAdd(value, rank)) return;
        var after = _order.FindIndex(v => _rank[v] > rank);
        if (after < 0) _order.Add(value);
        else _order.Insert(after, value);
    }
}
