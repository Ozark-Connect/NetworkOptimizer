using NetworkOptimizer.Web.Services.Monitoring.LiveTraffic;

namespace NetworkOptimizer.Web.Components.Shared.Monitoring.LiveTraffic;

/// <summary>
/// While the pointer is on a live list (and briefly after, or after a touch), rows keep their places
/// so they can be clicked; the values in them keep updating. A change the user asked for (sort,
/// filter, grouping, Active or Ended) takes effect at once: the held order only covers refreshes of
/// the same query.
/// </summary>
public sealed class LiveOrderHold
{
    private static readonly TimeSpan HoldAfterLeave = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan HoldAfterTouch = TimeSpan.FromSeconds(4);

    private bool _pointerIn;
    private DateTime _holdUntil = DateTime.MinValue;
    private string? _query;
    private List<string>? _flows;
    private List<string>? _groups;

    public bool Held => _pointerIn || DateTime.UtcNow < _holdUntil;

    public void PointerIn() => _pointerIn = true;

    public void PointerOut()
    {
        _pointerIn = false;
        _holdUntil = DateTime.UtcNow + HoldAfterLeave;
    }

    public void Touched() => _holdUntil = DateTime.UtcNow + HoldAfterTouch;

    /// <summary>Keeps the last order while held and the query is unchanged; records this order either way.</summary>
    public LiveTrafficView Apply(LiveTrafficView view, string query)
    {
        var sameQuery = query == _query;
        _query = query;
        if (Held && sameQuery)
        {
            view = view with
            {
                Flows = Reorder(view.Flows, f => f.Key, _flows),
                Groups = Reorder(view.Groups, g => g.Name, _groups),
            };
        }
        _flows = view.Flows.Select(f => f.Key).ToList();
        _groups = view.Groups.Select(g => g.Name).ToList();
        return view;
    }

    /// <summary>Rows that were shown keep their places; new rows follow, in the order they came.</summary>
    private static List<T> Reorder<T>(IReadOnlyList<T> rows, Func<T, string> key, List<string>? held)
    {
        if (held == null) return rows.ToList();
        var place = held.Select((k, i) => (k, i)).ToDictionary(p => p.k, p => p.i);
        return rows.Select((r, i) => (r, i))
            .OrderBy(p => place.TryGetValue(key(p.r), out var at) ? at : int.MaxValue)
            .ThenBy(p => p.i)
            .Select(p => p.r)
            .ToList();
    }
}
