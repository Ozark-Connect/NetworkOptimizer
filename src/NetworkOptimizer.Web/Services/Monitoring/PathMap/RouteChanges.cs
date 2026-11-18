using NetworkOptimizer.Threats.Enrichment;

namespace NetworkOptimizer.Web.Services.Monitoring.PathMap;

/// <summary>
/// Whether a stored path shift is a route change, and the Path Map's route-change events. Each trace's
/// signature is built with the placement it had when stored, so a hop that gains a hostname, a Set
/// location, or a better placement rule between two traces reads as the route moving. Here both routes
/// are placed alike first: every address takes the strongest place either route gave it.
/// </summary>
public static class RouteChanges
{
    /// <summary>Shifts on one network between the same places this close together are one event.</summary>
    internal static readonly TimeSpan GroupWindow = TimeSpan.FromHours(1);

    /// <summary>Whether the two routes still cross different places or networks once placed alike.</summary>
    public static bool IsReal(PlacedRoute from, PlacedRoute to)
    {
        var (a, b) = Aligned(from, to);
        var before = RoutePlacer.Signature(a);
        var after = RoutePlacer.Signature(b);
        return before != null && after != null && RouteSignature.Differs(before, after);
    }

    /// <summary>
    /// Both routes with one place and one network per address: the strongest place either gave it
    /// (<see cref="PathMapBuilder.Strength"/>), the newer route's on a tie, and the newer route's network.
    /// </summary>
    internal static (PlacedRoute From, PlacedRoute To) Aligned(PlacedRoute from, PlacedRoute to)
    {
        var places = new Dictionary<string, HopLocationView>(StringComparer.OrdinalIgnoreCase);
        foreach (var (address, view) in to.Locations.Concat(from.Locations))
        {
            if (!places.TryGetValue(address, out var cur) || PathMapBuilder.Strength(view) < PathMapBuilder.Strength(cur))
                places[address] = view;
        }
        var asns = new Dictionary<string, AsnLookup?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (address, asn) in to.Asns.Concat(from.Asns))
        {
            if (!asns.TryGetValue(address, out var cur) || (cur == null && asn != null)) asns[address] = asn;
        }

        PlacedRoute Apply(PlacedRoute r) => r with
        {
            Locations = r.Locations.Keys.ToDictionary(k => k, k => places[k], StringComparer.OrdinalIgnoreCase),
            Asns = r.Asns.Keys.ToDictionary(k => k, k => asns[k], StringComparer.OrdinalIgnoreCase),
        };
        return (Apply(from), Apply(to));
    }

    /// <summary>The destination's lowest round trip on a route; null when the trace did not reach it.</summary>
    internal static double? EndRttMs(StoredRoute route) =>
        route.Destination == null ? null
            : route.Hops.LastOrDefault(h => string.Equals(h.Address, route.Destination, StringComparison.OrdinalIgnoreCase))?.MinRttMs;
}
