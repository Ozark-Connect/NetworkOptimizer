using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using NetworkOptimizer.Web.Services.Monitoring.PathLocalization;

namespace NetworkOptimizer.Web.Services.Monitoring.PathMap;

/// <summary>A target as the builder takes it.</summary>
/// <param name="Route">Its route as of the window's end, placed; null when never traced.</param>
/// <param name="ShiftFrom">The route before the latest path shift in the window, placed; null without one.</param>
/// <param name="ShiftAt">When that shift was first seen.</param>
/// <param name="Traced">The trace loop traces the target on its own: an Internet or Custom target, or an ISP or
/// Transit target discovery gave a row to. One that is not only ever appears on other targets' routes.</param>
/// <param name="ShiftTo">The route the shift went to, placed; <paramref name="Route"/> when not given.</param>
public sealed record PathMapTargetInput(
    string TargetId, string Name, string Category, string WanKey, string? WanLabel, bool Unpinned, string Address,
    PlacedRoute? Route, PlacedRoute? ShiftFrom, DateTime? ShiftAt, DateTime? CheckedAt, PathTargetState State,
    IReadOnlyList<double?> Spark, string ProbeUrl, bool Traced = true, PlacedRoute? ShiftTo = null);

/// <summary>Where a WAN's traces start: GeoLite2's city for its public address.</summary>
public sealed record PathMapStart(string City, string? CountryCode, double Latitude, double Longitude, string Label);

/// <summary>Path Localization's located segment on an episode in the window.</summary>
public sealed record PathMapLocated(string? LastCleanIp, string OnsetIp);

/// <summary>
/// Builds the Path Map from each target's placed route. Pure: everything comes in as arguments.
///
/// Routes merge into a tree rooted at each vantage's start: hops are keyed by placed city plus ASN,
/// never address (ECMP moves addresses between traces), and never city alone (two carriers in one
/// metro do not share fate). Hop health follows ISP Health's propagation rule (<see cref="ElevatedRun"/>):
/// a monitored hop's own elevation counts only when every target beyond it carries it too, and a hop's
/// own reply loss only as far as the hops beyond it lost as much (<see cref="HopReading.EffectiveLossPct"/>).
/// </summary>
public static class PathMapBuilder
{
    /// <summary>Probes a hop needs, pooled across the traces through it, before its loss is read.</summary>
    internal const int MinLossProbes = 9;

    internal const string StartSource = "start";

    public static PathMapView Build(
        IReadOnlyList<PathMapTargetInput> targets,
        IReadOnlyDictionary<string, PathMapStart> starts,
        IReadOnlyList<PathMapLocated> located,
        IspHealthOptions options,
        bool live,
        DateTime? asOf)
    {
        targets = OnePlacePerHop(targets);
        var graph = new Graph();
        var paths = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var silentBefore = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in targets)
        {
            if (t.Route == null) continue;
            var silent = new List<int>();
            var path = graph.Walk(t, t.Route, starts, silent);
            if (path.Count > 0)
            {
                paths[t.TargetId] = path;
                silentBefore[t.TargetId] = silent;
            }
        }

        // Where each target sits: an Internet or Custom target at its route's end, an ISP or Transit
        // target at the place its address answers on any route, else at its own route's end.
        var endNode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
        {
            if (IsRouter(t) && graph.NodeOfAddress(t.Address) is int at) endNode[t.TargetId] = at;
            else if (paths.TryGetValue(t.TargetId, out var p) && p.Count > 1) endNode[t.TargetId] = p[^1];
        }

        var absolved = Absolved(targets, options);
        var nodeLoss = NodeLoss(targets, graph, options);

        // Legs and the targets that cross each, per WAN.
        var legTargets = new Dictionary<(int From, int To, string Wan), List<string>>();
        var legSilent = new Dictionary<(int From, int To, string Wan), int>();
        foreach (var t in targets)
        {
            if (!paths.TryGetValue(t.TargetId, out var p)) continue;
            for (var i = 1; i < p.Count; i++)
            {
                var key = (p[i - 1], p[i], t.WanKey);
                if (!legTargets.TryGetValue(key, out var list)) legTargets[key] = list = new List<string>();
                if (!list.Contains(t.TargetId, StringComparer.OrdinalIgnoreCase)) list.Add(t.TargetId);
                var silent = silentBefore[t.TargetId][i];
                legSilent[key] = legSilent.TryGetValue(key, out var cur) ? Math.Min(cur, silent) : silent;
            }
        }

        var byId = targets.ToDictionary(t => t.TargetId, StringComparer.OrdinalIgnoreCase);
        var halos = Halos(legTargets, paths, byId, graph);

        // Located: the last clean hop's place to the onset's place, and a marker at the onset.
        var locatedNodes = new HashSet<int>();
        var locatedLegs = new HashSet<(int, int)>();
        foreach (var l in located)
        {
            if (graph.NodeOfAddress(l.OnsetIp) is not int onset) continue;
            locatedNodes.Add(onset);
            if (l.LastCleanIp != null && graph.NodeOfAddress(l.LastCleanIp) is int clean && clean != onset)
                locatedLegs.Add((clean, onset));
        }

        var placesForVia = graph.Nodes.Select(n => (n.City, n.Latitude, n.Longitude, MinRtt(n), n.Asn)).ToList();
        var legs = legTargets.Select(kv =>
        {
            var (from, to, wan) = kv.Key;
            var a = graph.Nodes[from];
            var b = graph.Nodes[to];
            var silentHops = legSilent.GetValueOrDefault(kv.Key);
            var (via, either) = silentHops > 0 ? Via(from, to, wan, legTargets.Select(t => (t.Key, t.Value.Count)), placesForVia) : (null, null);
            return new PathMapLeg(from, to, wan, kv.Value.Count, Weight(kv.Value.Count),
                GeoDistance.Kilometers(a.Latitude, a.Longitude, b.Latitude, b.Longitude),
                a.Source == "geolite2" || b.Source == "geolite2",
                halos.TryGetValue(kv.Key, out var halo) ? halo : null,
                locatedLegs.Contains((from, to)),
                silentHops,
                via, either);
        }).ToList();

        // Monitored hops' health on the node they answer at.
        var nodeHealth = new Dictionary<int, (PathTargetHealth Health, bool Absolved)>();
        foreach (var t in targets.Where(IsRouter))
        {
            if (graph.NodeOfAddress(t.Address) is not int n) continue;
            var isAbsolved = absolved.Contains(t.TargetId);
            var health = isAbsolved ? PathTargetHealth.Normal : t.State.Health;
            if (!nodeHealth.TryGetValue(n, out var cur) || health > cur.Health)
                nodeHealth[n] = (health, isAbsolved || (cur.Absolved && health <= cur.Health));
        }

        var nodes = graph.Nodes.Select((n, i) => new PathMapNode(
            i, n.Latitude, n.Longitude, n.City, n.CountryCode, n.Asn, n.AsnName, n.Source, n.Hops,
            nodeHealth.TryGetValue(i, out var h) ? h.Health : null,
            nodeHealth.TryGetValue(i, out var h2) && h2.Absolved,
            nodeLoss.TryGetValue(i, out var loss) ? loss : null,
            locatedNodes.Contains(i))).ToList();

        var routeChanges = RouteChangesOf(targets, starts);
        var changeOf = routeChanges.SelectMany(c => c.Targets.Select(m => (m.TargetId, c.Id)))
            .ToDictionary(x => x.TargetId, x => x.Id, StringComparer.OrdinalIgnoreCase);
        var outTargets = targets.Select(t =>
        {
            var isAbsolved = absolved.Contains(t.TargetId);
            var health = isAbsolved ? PathTargetHealth.Normal : t.State.Health;
            return new PathMapTarget(
                t.TargetId, t.Name, t.Category, t.WanKey, t.WanLabel, t.Route?.Route.Vantage ?? "server", t.Unpinned, t.Address,
                endNode.TryGetValue(t.TargetId, out var e) ? e : null,
                PathFor(t, paths, endNode),
                health, isAbsolved, t.State.BaselineRttMs, t.State.RttMs, t.State.RiseMs, t.State.LossPct,
                t.State.ChangedAt, t.CheckedAt, changeOf.GetValueOrDefault(t.TargetId), t.Spark, t.ProbeUrl);
        }).ToList();

        var problems = outTargets
            .Where(t => t.Health is PathTargetHealth.Elevated or PathTargetHealth.Degraded or PathTargetHealth.Unreachable)
            .OrderByDescending(t => t.Health)
            .ThenByDescending(t => t.RiseMs ?? 0)
            .ThenByDescending(t => t.LossPct ?? 0)
            .Select(t => t.TargetId)
            .ToList();

        return new PathMapView(live, asOf, nodes, legs, outTargets, problems,
            targets.Count(t => !endNode.ContainsKey(t.TargetId)),
            // Waiting only for a trace the loop will run: a router already drawn on another route, or one
            // the loop never traces on its own, waits for nothing.
            WaitingTargets: targets.Count(t => t.Route == null && t.Traced && !endNode.ContainsKey(t.TargetId)),
            RouteChanges: routeChanges);
    }

    /// <summary>
    /// One place per hop across the whole map. Each route is placed on its own, and a router's
    /// evidence differs by route: a trace that ends at it (an ISP or Transit target traced alone)
    /// has no neighbors to anchor it, while a trace through it does. Every route takes the
    /// strongest placement any route gave the address: set by hand, then its hostname, then a
    /// nearby hop, then GeoLite2. Ties keep the first.
    /// </summary>
    internal static IReadOnlyList<PathMapTargetInput> OnePlacePerHop(IReadOnlyList<PathMapTargetInput> targets)
    {
        var best = new Dictionary<string, HopLocationView>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in targets.SelectMany(t => new[] { t.Route, t.ShiftFrom, t.ShiftTo }).Where(r => r != null))
        {
            foreach (var (address, view) in route!.Locations)
            {
                if (!best.TryGetValue(address, out var cur) || Strength(view) < Strength(cur)) best[address] = view;
            }
        }

        PlacedRoute? Apply(PlacedRoute? route) => route == null ? null : route with
        {
            Locations = route.Locations.ToDictionary(kv => kv.Key, kv => best[kv.Key], StringComparer.OrdinalIgnoreCase),
        };
        return targets.Select(t => t with { Route = Apply(t.Route), ShiftFrom = Apply(t.ShiftFrom), ShiftTo = Apply(t.ShiftTo) }).ToList();
    }

    // A city beats a country alone whatever placed it; among cities, the source's own order.
    internal static int Strength(HopLocationView v) => v.City is { Length: > 0 } ? (int)v.Source : 10;

    /// <summary>An ISP or Transit target: a router on the path, drawn on the trunk.</summary>
    internal static bool IsRouter(PathMapTargetInput t) => t.Category is MonitoringLinks.AccessIspCategory or MonitoringLinks.TransitCategory;

    /// <summary>
    /// For a stretch whose hops did not answer: where it most likely went. Candidates answer between the stretch's
    /// two round trips and fit them (<see cref="RouterHostnameLocator.KmPerMsRtt"/> per ms of the gap, plus 100 km).
    /// Places other routes on the same WAN reach from the start and leave for the end come first, the one the most
    /// routes use picked (on the test sites the named place was the hidden hop 168 times in 190). Without one, the
    /// places other routes enter the end's network at from the start. No ranking of several of those beat a coin flip
    /// (most used 10 of 19), so the line is drawn through the least detour, which reads as a route on the map, and the
    /// hover names them all.
    /// </summary>
    internal static (int? Pick, IReadOnlyList<string>? Either) Via(int from, int to, string wan,
        IEnumerable<((int From, int To, string Wan) Leg, int Targets)> legs,
        IReadOnlyList<(string City, double Latitude, double Longitude, double? RttMs, int? Asn)> nodes)
    {
        var onWan = legs.Where(l => l.Leg.Wan == wan).ToDictionary(l => (l.Leg.From, l.Leg.To), l => l.Targets);
        if (nodes[from].RttMs is not double startRtt || nodes[to].RttMs is not double endRtt) return (null, null);
        var budget = Math.Max(0, endRtt - startRtt) * RouterHostnameLocator.KmPerMsRtt + 100;
        // Only a place some route continues past: a place routes only end at (another target's destination) is not
        // a router anything goes through (us-west-1's DynamoDB end drew us-west-2 through San Jose).
        var passedThrough = onWan.Keys.Select(k => k.Item1).ToHashSet();
        // Each half must be possible on its own: the place answers no sooner than the start plus the way there, and the
        // end no sooner than the place plus the way on (Cox Dallas at 16.6 ms cannot be reached through Cox Kansas City
        // at 13.4 ms, 720 km away).
        bool Reachable(double fromRtt, int a, double toRtt, int b) => toRtt + 1 >= fromRtt
            + GeoDistance.Kilometers(nodes[a].Latitude, nodes[a].Longitude, nodes[b].Latitude, nodes[b].Longitude) / RouterHostnameLocator.KmPerMsRtt;
        bool Fits(int c) => c != from && c != to && passedThrough.Contains(c) && nodes[c].RttMs is double rtt && rtt >= startRtt && rtt <= endRtt
            && GeoDistance.Kilometers(nodes[from].Latitude, nodes[from].Longitude, nodes[c].Latitude, nodes[c].Longitude)
                + GeoDistance.Kilometers(nodes[c].Latitude, nodes[c].Longitude, nodes[to].Latitude, nodes[to].Longitude) <= budget
            && Reachable(startRtt, from, rtt, c) && Reachable(rtt, c, endRtt, to);
        var leaving = onWan.Where(l => l.Key.Item1 == from && Fits(l.Key.Item2)).ToList();
        var through = leaving.Where(l => onWan.ContainsKey((l.Key.Item2, to))).ToList();
        if (through.Count > 0)
            return (through.OrderByDescending(l => l.Value).ThenBy(l => nodes[l.Key.Item2].City, StringComparer.OrdinalIgnoreCase).First().Key.Item2, null);
        if (nodes[to].Asn is not int network) return (null, null);
        double Detour(int c) =>
            GeoDistance.Kilometers(nodes[from].Latitude, nodes[from].Longitude, nodes[c].Latitude, nodes[c].Longitude)
            + GeoDistance.Kilometers(nodes[c].Latitude, nodes[c].Longitude, nodes[to].Latitude, nodes[to].Longitude);
        var entries = leaving.Where(l => nodes[l.Key.Item2].Asn == network).Select(l => l.Key.Item2)
            .OrderBy(Detour).ToList();
        if (entries.Count == 0) return (null, null);
        if (entries.Count == 1) return (entries[0], null);
        // The drawn place first, then the rest.
        var cities = entries.Select(c => nodes[c].City).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return (entries[0], cities);
    }

    // A place's lowest round trip among its hops; a start point (no hops) is 0.
    private static double? MinRtt(Graph.Node n) => n.Source == StartSource ? 0 : n.Hops.Min(h => h.RttMs);

    /// <summary>
    /// The nodes a target's hover lights: its own route, else, for an ISP or Transit router never traced on its own,
    /// the way the first route crossing it reaches it, from the start up to its place.
    /// </summary>
    private static IReadOnlyList<int> PathFor(PathMapTargetInput t, Dictionary<string, List<int>> paths, Dictionary<string, int> endNode)
    {
        if (paths.TryGetValue(t.TargetId, out var own)) return own;
        if (!IsRouter(t) || !endNode.TryGetValue(t.TargetId, out var at)) return [];
        foreach (var p in paths.Values)
        {
            var i = p.IndexOf(at);
            if (i > 0) return p.Take(i + 1).ToList();
        }
        return [];
    }

    /// <summary>Three capped steps by the targets crossing a leg: 1, 2 to 5, 6 or more.</summary>
    internal static int Weight(int targets) => targets >= 6 ? 3 : targets >= 2 ? 2 : 1;

    internal static bool OverBaseline(PathTargetHealth h) =>
        h is PathTargetHealth.Elevated or PathTargetHealth.Degraded or PathTargetHealth.Unreachable;

    /// <summary>
    /// The ISP and Transit targets whose own elevation is not the path's: on some route through them,
    /// the target at the end stays clean (<see cref="ElevatedRun.Onset"/>). A router answers its own
    /// probes at low priority, so its delay and loss count only when the hops beyond it carry them.
    /// </summary>
    internal static HashSet<string> Absolved(IReadOnlyList<PathMapTargetInput> targets, IspHealthOptions options)
    {
        var routers = targets.Where(IsRouter).GroupBy(t => t.Address, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var absolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets.Where(t => !IsRouter(t) && t.Route != null))
        {
            // The monitored hops this route crosses, nearest first, then the destination.
            var chain = new List<(PathMapTargetInput? Router, bool? Elevated)>();
            foreach (var hop in t.Route!.Route.Hops.Where(h => h.Address != null && h.SeenAt == null))
            {
                if (!routers.TryGetValue(hop.Address!, out var at)) continue;
                foreach (var r in at) chain.Add((r, Elevated(r.State.Health)));
            }
            if (chain.Count == 0) continue;
            chain.Add((null, Elevated(t.State.Health)));
            var onset = ElevatedRun.Onset(chain.Count, i => chain[i].Elevated);
            for (var i = 0; i < chain.Count - 1; i++)
            {
                if (chain[i].Elevated == true && (onset == null || i < onset)) absolved.Add(chain[i].Router!.TargetId);
            }
        }
        return absolved;
    }

    private static bool? Elevated(PathTargetHealth h) => h == PathTargetHealth.Unknown ? null : OverBaseline(h);

    /// <summary>
    /// Each node's forwarding loss: its hops' loss pooled across the traces through it, never above
    /// the least-lossy hop beyond it on any of them (<see cref="HopReading.EffectiveLossPct"/>).
    /// Null below <see cref="MinLossProbes"/> probes.
    /// </summary>
    private static Dictionary<int, double> NodeLoss(IReadOnlyList<PathMapTargetInput> targets, Graph graph, IspHealthOptions options)
    {
        var sent = new Dictionary<int, int>();
        var lost = new Dictionary<int, double>();
        foreach (var t in targets)
        {
            if (t.Route == null) continue;
            // A hop filled in from an earlier trace was not probed by this one: its loss is not this trace's.
            foreach (var reading in Readings(t.Route.Route.Hops.Where(h => h.SeenAt == null).ToList()))
            {
                if (reading.Address == null || graph.NodeOfAddress(reading.Address) is not int n || reading.Sent == 0) continue;
                sent[n] = sent.GetValueOrDefault(n) + reading.Sent;
                lost[n] = lost.GetValueOrDefault(n) + reading.EffectiveLossPct * reading.Sent / 100.0;
            }
        }
        return sent.Where(kv => kv.Value >= MinLossProbes)
            .ToDictionary(kv => kv.Key, kv => 100.0 * lost[kv.Key] / kv.Value);
    }

    /// <summary>A route's hops as Path Localization reads them, each with the loss floor of the hops beyond it.</summary>
    internal static List<HopReading> Readings(IReadOnlyList<StoredRouteHop> hops)
    {
        var readings = hops.Select(h => new HopReading
        {
            Ttl = h.Ttl,
            Address = h.Address,
            Hostname = h.Hostname,
            Sent = Math.Max(h.Probes, h.Responses),
            Responses = h.Responses,
            MinRttMs = h.MinRttMs,
        }).ToList();
        double? floor = null;
        for (var i = readings.Count - 1; i >= 0; i--)
        {
            readings[i] = readings[i] with { DownstreamFloorPct = floor };
            if (readings[i].Responses > 0)
                floor = floor is double f ? Math.Min(f, readings[i].LossPct) : readings[i].LossPct;
        }
        return readings;
    }

    /// <summary>
    /// The inferred halo: a leg with two or more targets beyond it, every one over baseline, while
    /// another target through the same place is clean. Only the shallowest leg of such a set carries
    /// it. The sentence is an observation, never a cause.
    /// </summary>
    private static Dictionary<(int, int, string), string> Halos(
        Dictionary<(int From, int To, string Wan), List<string>> legTargets,
        Dictionary<string, List<int>> paths,
        Dictionary<string, PathMapTargetInput> byId,
        Graph graph)
    {
        static bool IsEnd(PathMapTargetInput t) => !IsRouter(t);
        var candidates = new Dictionary<(int, int, string), (HashSet<string> Beyond, string Text)>();
        foreach (var (key, ids) in legTargets)
        {
            var beyond = ids.Where(id => byId.TryGetValue(id, out var t) && IsEnd(t)).ToList();
            if (beyond.Count < 2 || !beyond.All(id => OverBaseline(byId[id].State.Health))) continue;
            var through = paths.Where(p => byId.TryGetValue(p.Key, out var t) && IsEnd(t) && t.WanKey == key.Wan
                    && p.Value.Contains(key.From) && !beyond.Contains(p.Key, StringComparer.OrdinalIgnoreCase))
                .Select(p => byId[p.Key]).ToList();
            var clean = through.Count(t => t.State.Health == PathTargetHealth.Normal);
            if (clean == 0) continue;
            var place = graph.Nodes[key.From].City;
            var text = $"All {beyond.Count} targets beyond {place} are slow, {clean} {(clean == 1 ? "other" : "others")} through {place} {(clean == 1 ? "is" : "are")} not";
            candidates[key] = (beyond.ToHashSet(StringComparer.OrdinalIgnoreCase), text);
        }
        // A leg whose targets a candidate further up already covers is part of the same observation.
        return candidates
            .Where(c => !candidates.Any(o => !o.Key.Equals(c.Key) && o.Value.Beyond.IsSupersetOf(c.Value.Beyond)
                && (o.Value.Beyond.Count > c.Value.Beyond.Count || o.Key.Item2 == c.Key.Item1)))
            .ToDictionary(c => c.Key, c => c.Value.Text);
    }

    /// <summary>
    /// The window's route changes, one per network and pair of old and new places: a transit network
    /// re-routing moves many targets at once, and each target's shift alone says less than the group.
    /// Shifts in one group more than <see cref="RouteChanges.GroupWindow"/> apart are separate changes.
    /// </summary>
    internal static IReadOnlyList<PathMapRouteChange> RouteChangesOf(IReadOnlyList<PathMapTargetInput> targets,
        IReadOnlyDictionary<string, PathMapStart> starts)
    {
        var shifts = new List<(string Key, DateTime At, string WanKey, string? Network, List<string> Before, List<string> After,
            double[]? Diverge, PathMapRouteChangeTarget Target)>();
        foreach (var t in targets)
        {
            var to = t.ShiftTo ?? t.Route;
            if (t.ShiftFrom == null || t.ShiftAt is not DateTime at || to == null) continue;
            var oldKeys = Graph.Keys(t.ShiftFrom);
            var newKeys = Graph.Keys(to);
            var oldSet = oldKeys.Select(k => k.Key).ToHashSet();
            var newSet = newKeys.Select(k => k.Key).ToHashSet();
            var removed = oldKeys.Where(k => !newSet.Contains(k.Key)).ToList();
            var added = newKeys.Where(k => !oldSet.Contains(k.Key)).ToList();
            if (removed.Count == 0 && added.Count == 0) continue;

            var points = new List<double[]>();
            double[]? diverge = null;
            if (starts.TryGetValue(t.WanKey, out var s))
            {
                points.Add([s.Latitude, s.Longitude]);
                diverge = [s.Latitude, s.Longitude];
            }
            points.AddRange(oldKeys.Select(k => new[] { k.Latitude, k.Longitude }));
            // The last place both routes share, in order from the start.
            for (var i = 0; i < Math.Min(oldKeys.Count, newKeys.Count) && oldKeys[i].Key == newKeys[i].Key; i++)
                diverge = [oldKeys[i].Latitude, oldKeys[i].Longitude];

            var before = removed.Select(k => k.City).Distinct().ToList();
            var after = added.Select(k => k.City).Distinct().ToList();
            var network = (added.Count > 0 ? added : removed).Select(k => k.AsnName).FirstOrDefault(n => !string.IsNullOrEmpty(n));
            var key = $"{t.WanKey}|{network}|{string.Join(">", removed.Select(k => k.Key))}|{string.Join(">", added.Select(k => k.Key))}";
            shifts.Add((key, at, t.WanKey, network, before, after, diverge, new PathMapRouteChangeTarget(t.TargetId, t.Name,
                RouteChanges.EndRttMs(t.ShiftFrom.Route), RouteChanges.EndRttMs(to.Route), points)));
        }

        var changes = new List<PathMapRouteChange>();
        foreach (var group in shifts.GroupBy(s => s.Key))
        {
            var ordered = group.OrderBy(s => s.At).ToList();
            var start = 0;
            for (var i = 1; i <= ordered.Count; i++)
            {
                if (i < ordered.Count && ordered[i].At - ordered[start].At <= RouteChanges.GroupWindow) continue;
                var first = ordered[start];
                var members = ordered.Skip(start).Take(i - start).ToList();
                changes.Add(new PathMapRouteChange(
                    $"{first.At.Ticks}|{group.Key}", first.At, first.WanKey, first.Network, first.Before, first.After, first.Diverge,
                    members.Select(m => m.Target).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList()));
                start = i;
            }
        }
        return changes.OrderByDescending(c => c.At).ToList();
    }

    /// <summary>The tree under construction.</summary>
    private sealed class Graph
    {
        public sealed class Node
        {
            public required string Key { get; init; }
            public double Latitude { get; init; }
            public double Longitude { get; init; }
            public required string City { get; init; }
            public string? CountryCode { get; init; }
            public int? Asn { get; init; }
            public string? AsnName { get; init; }
            public required string Source { get; set; }
            public List<PathMapHop> Hops { get; } = new();
        }

        public readonly List<Node> Nodes = new();
        private readonly Dictionary<string, int> _byKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _byAddress = new(StringComparer.OrdinalIgnoreCase);

        public int? NodeOfAddress(string address) => _byAddress.TryGetValue(address, out var n) ? n : null;

        /// <summary>One placed hop of a route: its merge key and place.</summary>
        public sealed record PlacedHop(string Key, string Address, string? Hostname, double? RttMs, string City, string? CountryCode,
            double Latitude, double Longitude, int? Asn, string? AsnName, string Source, string PlacedBy);

        /// <summary>A route's placed hops, nearest first, consecutive repeats of one place collapsed.</summary>
        public static List<PlacedHop> Keys(PlacedRoute route)
        {
            var list = new List<PlacedHop>();
            foreach (var h in route.Route.Hops)
            {
                if (h.Address == null || !route.Locations.TryGetValue(h.Address, out var at)
                    || at is not { City.Length: > 0, Latitude: double lat, Longitude: double lon }) continue;
                var asn = route.Asns.TryGetValue(h.Address, out var a) ? a : null;
                var key = $"{at.City}|{at.CountryCode}|{asn?.Asn}";
                if (list.Count > 0 && list[^1].Key == key) continue;
                list.Add(new PlacedHop(key, h.Address, h.Hostname, h.MinRttMs, at.City, at.CountryCode, lat, lon,
                    asn?.Asn, PathTraceAnalyzer.AsnLabel(asn), SourceName(at.Source), at.Tooltip));
            }
            return list;
        }

        /// <summary>Adds a route and returns its nodes from the root. Empty when nothing on it is placed.</summary>
        /// <param name="silentBefore">Filled with, per node of the path, the hops that answered no probe between the
        /// node before it and this one.</param>
        public List<int> Walk(PathMapTargetInput t, PlacedRoute route, IReadOnlyDictionary<string, PathMapStart> starts,
            List<int> silentBefore)
        {
            var path = new List<int>();
            var placed = Keys(route);
            if (placed.Count == 0) return path;
            var silent = 0;
            void Step(int node)
            {
                if (path.Count > 0 && path[^1] == node) return;
                path.Add(node);
                silentBefore.Add(silent);
            }

            // Each vantage on each WAN is its own root: an agent's traces leave from another place.
            if (starts.TryGetValue(t.WanKey, out var s))
            {
                var rootKey = $"start|{route.Route.Vantage}|{t.WanKey}";
                Step(Add(rootKey, () => new Node
                {
                    Key = rootKey, Latitude = s.Latitude, Longitude = s.Longitude, City = s.City,
                    CountryCode = s.CountryCode, Source = StartSource,
                }));
            }

            // Every responding hop is listed at its place, placed or not: an unplaced hop between two
            // placed ones is in the place before it. A silent hop counts toward the next step.
            foreach (var hop in route.Route.Hops.OrderBy(h => h.Ttl))
            {
                if (hop.Address == null)
                {
                    silent++;
                    continue;
                }
                var p = placed.FirstOrDefault(x => string.Equals(x.Address, hop.Address, StringComparison.OrdinalIgnoreCase));
                if (p == null)
                {
                    var key = route.Locations.TryGetValue(hop.Address!, out var at) && at is { City.Length: > 0 }
                        ? $"{at.City}|{at.CountryCode}|{(route.Asns.TryGetValue(hop.Address!, out var a) ? a?.Asn : null)}"
                        : null;
                    // A hop in a place already on the route (a repeat collapsed out of Keys) joins it.
                    if (key != null && _byKey.TryGetValue(key, out var same) && path.Contains(same))
                    {
                        Attach(same, hop, at!.Tooltip, at.Source);
                        Step(same);
                        silent = 0;
                    }
                    continue;
                }
                var index = Add(p.Key, () => new Node
                {
                    Key = p.Key, Latitude = p.Latitude, Longitude = p.Longitude, City = p.City, CountryCode = p.CountryCode,
                    Asn = p.Asn, AsnName = p.AsnName, Source = p.Source,
                });
                Attach(index, hop, p.PlacedBy, route.Locations[hop.Address!].Source);
                Step(index);
                silent = 0;
            }
            return path;
        }

        private void Attach(int index, StoredRouteHop hop, string placedBy, HopLocationSource source)
        {
            var node = Nodes[index];
            _byAddress.TryAdd(hop.Address!, index);
            if (Rank(SourceName(source)) < Rank(node.Source)) node.Source = SourceName(source);
            var existing = node.Hops.FindIndex(h => string.Equals(h.Address, hop.Address, StringComparison.OrdinalIgnoreCase));
            if (existing < 0)
            {
                node.Hops.Add(new PathMapHop(hop.Address!, hop.Hostname, hop.MinRttMs, placedBy, hop.SeenAt));
                return;
            }
            var cur = node.Hops[existing];
            // A hop some trace heard this time drops the earlier sighting and its round trip.
            if (hop.SeenAt == null && cur.SeenAt != null) cur = cur with { SeenAt = null, RttMs = hop.MinRttMs };
            else if (hop.SeenAt == null || cur.SeenAt != null)
            {
                if (hop.MinRttMs is double rtt && (cur.RttMs is not double r || rtt < r)) cur = cur with { RttMs = rtt };
                if (hop.SeenAt is DateTime seen && cur.SeenAt is DateTime was && seen > was) cur = cur with { SeenAt = seen };
            }
            node.Hops[existing] = cur;
        }

        private int Add(string key, Func<Node> create)
        {
            if (_byKey.TryGetValue(key, out var index)) return index;
            Nodes.Add(create());
            return _byKey[key] = Nodes.Count - 1;
        }

        private static string SourceName(HopLocationSource source) => TraceMapPath.SourceName(source);

        private static int Rank(string source) => TraceMapPath.Rank(source);
    }
}
