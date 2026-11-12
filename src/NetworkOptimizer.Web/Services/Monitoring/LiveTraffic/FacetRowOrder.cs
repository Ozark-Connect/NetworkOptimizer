namespace NetworkOptimizer.Web.Services.Monitoring.LiveTraffic;

/// <summary>
/// The rows of an open chip editor, in an order that never changes under the pointer. Set when the editor
/// opens (the chip's values first, then by count, idle clients last); ticking a value or a refresh moving
/// the counts leaves every row where it is. A value seen later is added at the end, or above the idle
/// clients when it is moving, and one that drops out of the counts keeps its row.
/// </summary>
public sealed class FacetRowOrder
{
    private readonly List<string> _order = new();
    private readonly HashSet<string> _placed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _idle = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every value so far, in row order, after placing any the chip or the counts add.</summary>
    public IReadOnlyList<string> Arrange(IEnumerable<string> selected, IEnumerable<LiveFacetValue> facets)
    {
        foreach (var v in selected.Order(StringComparer.OrdinalIgnoreCase)) Place(v, idle: false);
        foreach (var f in facets.OrderBy(f => f.Idle)) Place(f.Value, f.Idle);
        return _order;
    }

    /// <summary>Placed as an idle client, so it reads under "Not moving now" when the counts no longer list it.</summary>
    public bool WasIdle(string value) => _idle.Contains(value);

    private void Place(string value, bool idle)
    {
        if (!_placed.Add(value)) return;
        if (idle)
        {
            _idle.Add(value);
            _order.Add(value);
            return;
        }
        var firstIdle = _order.FindIndex(_idle.Contains);
        if (firstIdle < 0) _order.Add(value);
        else _order.Insert(firstIdle, value);
    }
}
