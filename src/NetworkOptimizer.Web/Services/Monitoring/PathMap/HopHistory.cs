namespace NetworkOptimizer.Web.Services.Monitoring.PathMap;

/// <summary>
/// Fills in hops a trace got no answer from, with what other traces from the same vantage saw there.
/// A router that answers only some of the time (rate limiting its own replies) is still on the path:
/// another trace that answered at the same hop number, between the same two answering neighbors,
/// crossed the same stretch and saw it. The filled hop carries when it was seen
/// (<see cref="StoredRouteHop.SeenAt"/>) and never counts as measured.
/// </summary>
public static class HopHistory
{
    /// <summary>A sighting older than this, relative to the trace it fills, is not used: the path may have moved.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    /// <summary>
    /// The route with each silent hop filled from the newest other trace that answered at that hop number and
    /// agrees with the route on the nearest answering hop on each side. The one before must be the same address at
    /// the same hop number. The one after must be too, or, when it is the trace's destination, the other trace's
    /// destination on the same network: a hostname target rotates its address, and its hop count with it.
    /// A silent hop with no answering hop after it (the trace ran out) is left silent.
    /// </summary>
    /// <param name="history">Other traces, any target; only those from the same vantage and bind are used.</param>
    /// <param name="networkOf">The network (ASN) of an address, to match two destinations; null matches only the same address.</param>
    public static StoredRoute Fill(StoredRoute route, IEnumerable<StoredRoute> history, Func<string, int?>? networkOf = null)
    {
        var hops = route.Hops.OrderBy(h => h.Ttl).ToList();
        if (!hops.Any(h => h.Address == null)) return route;
        var pool = history
            .Where(r => !ReferenceEquals(r, route) && r.Vantage == route.Vantage && r.Bind == route.Bind)
            .OrderByDescending(r => r.CheckedAt)
            .Select(r => (Route: r, ByTtl: r.Hops.Where(h => h.Address != null).GroupBy(h => h.Ttl).ToDictionary(g => g.Key, g => g.First())))
            .ToList();
        if (pool.Count == 0) return route;

        var filled = hops.ToList();
        for (var i = 0; i < hops.Count; i++)
        {
            if (hops[i].Address != null) continue;
            var before = hops.Take(i).LastOrDefault(h => h.Address != null);
            var after = hops.Skip(i + 1).FirstOrDefault(h => h.Address != null);
            if (before == null || after == null) continue;
            foreach (var (r, byTtl) in pool)
            {
                if (!Same(byTtl, before) || !byTtl.TryGetValue(hops[i].Ttl, out var seen)) continue;
                if (!Same(byTtl, after) && !SameDestination(route, after, r, networkOf)) continue;
                var at = seen.SeenAt ?? r.CheckedAt;
                if (route.CheckedAt - at > MaxAge) continue;
                filled[i] = seen with { SeenAt = at };
                break;
            }
        }
        return route with { Hops = filled };
    }

    private static bool Same(Dictionary<int, StoredRouteHop> byTtl, StoredRouteHop hop) =>
        byTtl.TryGetValue(hop.Ttl, out var other) && string.Equals(other.Address, hop.Address, StringComparison.OrdinalIgnoreCase);

    // The hop after the gap is this trace's destination, and the other trace reached a destination on the same network.
    private static bool SameDestination(StoredRoute route, StoredRouteHop after, StoredRoute other, Func<string, int?>? networkOf)
    {
        if (route.Destination == null || other.Destination == null
            || !string.Equals(after.Address, route.Destination, StringComparison.OrdinalIgnoreCase)
            || !other.Hops.Any(h => string.Equals(h.Address, other.Destination, StringComparison.OrdinalIgnoreCase))) return false;
        if (string.Equals(other.Destination, route.Destination, StringComparison.OrdinalIgnoreCase)) return true;
        return networkOf != null && networkOf(route.Destination) is int a && networkOf(other.Destination) == a;
    }
}
