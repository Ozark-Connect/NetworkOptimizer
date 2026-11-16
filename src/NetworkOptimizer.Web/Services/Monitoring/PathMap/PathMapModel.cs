using NetworkOptimizer.Web.Services.Monitoring.PathLocalization;

namespace NetworkOptimizer.Web.Services.Monitoring.PathMap;

/// <summary>The Path Map as the page draws it: places, the legs between them, and the targets at their ends.</summary>
/// <param name="Live">The window ends now. A past window carries <paramref name="AsOf"/>.</param>
/// <param name="AsOf">The window's end, for a window that does not end now.</param>
public sealed record PathMapView(
    bool Live,
    DateTime? AsOf,
    IReadOnlyList<PathMapNode> Nodes,
    IReadOnlyList<PathMapLeg> Legs,
    IReadOnlyList<PathMapTarget> Targets,
    IReadOnlyList<string> Problems,
    int UntracedTargets,
    string? Notice = null,
    int WaitingTargets = 0,
    IReadOnlyList<PathMapRouteChange>? RouteChanges = null)
{
    public static PathMapView Empty(string? notice) => new(true, null, [], [], [], [], 0, notice);
}

/// <summary>One hop listed at a place.</summary>
/// <param name="PlacedBy">What placed it, as the Route Map's location tooltip says it.</param>
/// <param name="SeenAt">Set when no trace on the map heard it this time and it comes from an earlier trace that
/// did (<see cref="HopHistory"/>): when that trace ran.</param>
public sealed record PathMapHop(string Address, string? Hostname, double? RttMs, string PlacedBy, DateTime? SeenAt = null);

/// <summary>
/// One place on the map: hops in one city on one network (city and ASN, never address), or a vantage's start.
/// </summary>
/// <param name="Source">"start", "set", "hostname", "neighbor", or "geolite2": the strongest evidence among its hops.</param>
/// <param name="Health">The health of a monitored hop here (an ISP or Transit target), after absolving; null when none is.</param>
/// <param name="Absolved">A monitored hop here is slow or lossy on its own probes while every target beyond it is clean.</param>
/// <param name="LossPct">Forwarding loss the traces through it account for (each hop's own loss, lowered to the
/// least-lossy hop beyond it); null when too few probes crossed it.</param>
public sealed record PathMapNode(
    int Id, double Latitude, double Longitude, string City, string? CountryCode,
    int? Asn, string? AsnName, string Source, IReadOnlyList<PathMapHop> Hops,
    PathTargetHealth? Health = null, bool Absolved = false, double? LossPct = null, bool Located = false);

/// <summary>A drawn stretch between two places, for one WAN.</summary>
/// <param name="Weight">1 to 3, by the number of targets crossing it (capped steps).</param>
/// <param name="Unsure">An end is placed only by GeoLite2: drawn dashed.</param>
/// <param name="Halo">The inferred-halo sentence, when every target beyond it is over baseline while another branch is clean.</param>
/// <param name="Located">Path Localization's last clean hop to its first elevated hop.</param>
/// <param name="SilentHops">Hops between its two places that answered no probe, on the route that heard the most of
/// the stretch: 0 when some trace heard every hop of it. The route between the places is not known.</param>
/// <param name="Via">For a leg with silent hops: the node other routes most likely took between its ends
/// (<see cref="PathMapBuilder.Via"/>). The dotted leg is drawn through it; it counts toward nothing.</param>
/// <param name="ViaEither">When the evidence does not settle on one, the places it could be, said in the hover only.</param>
public sealed record PathMapLeg(int From, int To, string WanKey, int TargetCount, int Weight, double Kilometers,
    bool Unsure, string? Halo, bool Located, int SilentHops = 0, int? Via = null, IReadOnlyList<string>? ViaEither = null);

/// <summary>
/// A route change in the window: the targets whose routes left the same places for the same new ones, on
/// one network, within <see cref="RouteChanges.GroupWindow"/> (<see cref="PathMapBuilder.RouteChangesOf"/>).
/// </summary>
/// <param name="Id">Stable across polls, for the selection.</param>
/// <param name="At">When the first of its targets' new route was first seen.</param>
/// <param name="Network">The network the route changed on: the new places' network, else the old ones'.</param>
/// <param name="Before">The places only the old routes crossed, in order.</param>
/// <param name="After">The places only the new routes cross, in order.</param>
/// <param name="Diverge">The last place the old and new routes share, for the map marker.</param>
public sealed record PathMapRouteChange(string Id, DateTime At, string WanKey, string? Network,
    IReadOnlyList<string> Before, IReadOnlyList<string> After, double[]? Diverge, IReadOnlyList<PathMapRouteChangeTarget> Targets);

/// <summary>One target of a route change.</summary>
/// <param name="RttBeforeMs">The destination's lowest round trip on the trace before; null when it did not reach it.</param>
/// <param name="OldPoints">The old route's places from the root, drawn when the change is selected.</param>
public sealed record PathMapRouteChangeTarget(string TargetId, string Name, double? RttBeforeMs, double? RttAfterMs,
    IReadOnlyList<double[]> OldPoints);

/// <summary>A monitored target and where its route ends.</summary>
/// <param name="Category">The chart category: AccessIsp, Transit, InternetService, Custom.</param>
/// <param name="EndNode">The node it is drawn at; null when its route is not placed.</param>
/// <param name="Path">Its route's nodes, from the root, for the hover highlight.</param>
/// <param name="Unpinned">Its probes are pinned to no WAN: shown only in the all-WAN view.</param>
/// <param name="RiseMs">How far over its usual RTT it is now.</param>
/// <param name="RouteChange">The <see cref="PathMapRouteChange.Id"/> of its route change in the window; null without one.</param>
/// <param name="Spark">RTT per evaluation window over the chart window, for the hover card.</param>
public sealed record PathMapTarget(
    string TargetId, string Name, string Category, string WanKey, string? WanLabel, string Vantage, bool Unpinned,
    string Address, int? EndNode, IReadOnlyList<int> Path, PathTargetHealth Health, bool Absolved,
    double? BaselineRttMs, double? RttMs, double? RiseMs, double? LossPct, DateTime? ChangedAt, DateTime? CheckedAt,
    string? RouteChange, IReadOnlyList<double?> Spark, string ProbeUrl);
