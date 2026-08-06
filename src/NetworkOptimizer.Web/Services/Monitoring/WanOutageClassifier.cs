using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// What one WAN's monitored targets currently say about that WAN, classified for alerting.
/// </summary>
internal enum WanVerdictKind
{
    /// <summary>Nothing alert-worthy: everything reachable, or too little evidence to say.</summary>
    None,

    /// <summary>Part of the path beyond the access layer is out while the WAN still passes traffic.</summary>
    Partial,

    /// <summary>The WAN's internet is down: every destination failing, with or without the first hop.</summary>
    Total
}

/// <summary>
/// One WAN-scoped monitoring target's current state, as the WAN outage classifier sees it.
/// <paramref name="Failing"/> is the per-target offline state machine's verdict (consecutive
/// failed probes); <paramref name="Degraded"/> additionally includes sustained-loss targets,
/// and is what the partial pass keys on - a branch can be "out or degraded" without every
/// probe failing outright. <paramref name="Depth"/> is the trace-map hop number
/// (<see cref="int.MaxValue"/> when the trace map does not place the row, which also clears
/// <paramref name="KnownPosition"/>). <paramref name="AncestorIps"/> are the proven-upstream
/// monitored hop addresses from <see cref="UpstreamDiscovery.AncestorHopIps"/>.
/// </summary>
internal sealed record WanTargetSnapshot(
    string TargetId,
    MonitoringTargetType Type,
    string Name,
    string Address,
    bool Failing,
    bool Degraded,
    int Depth,
    bool KnownPosition,
    bool IsInternet,
    string? AsnLabel,
    int AsnNumber,
    IReadOnlySet<string> AncestorIps);

/// <summary>
/// The classifier's answer for one WAN on one evaluation pass. <paramref name="AccessDown"/>
/// distinguishes "access layer and out" from "upstream of the access layer" for a
/// <see cref="WanVerdictKind.Total"/> verdict. <paramref name="LastReachableHop"/> /
/// <paramref name="BrokenNetwork"/> carry <see cref="OutageDetector.AttributeBreak"/>'s
/// attribution for the upstream case; <paramref name="BranchLabel"/> names the shared
/// ancestor (or shared network) for a branch-shaped partial, null for an independent one.
/// </summary>
internal sealed record WanVerdict(
    WanVerdictKind Kind,
    bool AccessDown,
    string? LastReachableHop,
    string? BrokenNetwork,
    string? BranchLabel,
    int FailingCount,
    int TotalCount)
{
    public static readonly WanVerdict None = new(WanVerdictKind.None, false, null, null, null, 0, 0);
}

/// <summary>
/// Classifies one WAN's current target states into an outage verdict. Pure and stateless: the
/// evaluator owns freshness, confirmation counting and publishing; this owns only "what shape
/// is the failure". The attribution rules are inherited from <see cref="OutageDetector"/>
/// rather than restated: break naming goes through <see cref="OutageDetector.AttributeBreak"/>
/// (which refuses to anchor on off-map or internet-endpoint rows), and network independence
/// uses <see cref="OutageDetector.NetworkKey"/> so several regional endpoints of one provider
/// never read as several independent networks.
/// </summary>
internal static class WanOutageClassifier
{
    public static WanVerdict Classify(IReadOnlyList<WanTargetSnapshot> targets)
    {
        if (targets.Count == 0) return WanVerdict.None;

        var failing = targets.Where(t => t.Failing).ToList();
        var degraded = targets.Where(t => t.Degraded).ToList();
        var accessRows = targets.Where(t => t.Type == MonitoringTargetType.AccessIsp).ToList();

        // Access layer and out: every target on the WAN is failing, first hop included.
        if (failing.Count == targets.Count)
            return new WanVerdict(WanVerdictKind.Total, AccessDown: accessRows.Count > 0,
                null, null, null, failing.Count, targets.Count);

        // Upstream of the access layer: the first hop answers, everything beyond it is failing.
        // At least one failing internet destination is required - a set that is all transit
        // beyond the access hop can go probe-dark from rate limiting alone, and with no
        // destination monitored there is no evidence anything the user reaches is down.
        if (accessRows.Count > 0
            && accessRows.All(a => !a.Failing)
            && targets.Where(t => t.Type != MonitoringTargetType.AccessIsp).All(t => t.Failing)
            && targets.Any(t => t.IsInternet && t.Failing))
        {
            var (lastReachable, brokenNetwork) = AttributeUpstreamBreak(targets);
            return new WanVerdict(WanVerdictKind.Total, AccessDown: false,
                lastReachable, brokenNetwork, null, failing.Count, targets.Count);
        }

        // Partial pass, over the degraded set (offline or sustained loss). A partial only opens
        // when at least one internet destination is affected: transit hops routinely stop
        // answering probes with nothing wrong, so a transit-only picture with every destination
        // reachable is a rate-limited router, not an outage. Corroboration for a transit failure
        // is exactly a destination behind it also failing, which the branch pass below finds.
        if (degraded.Count == 0 || !degraded.Any(t => t.IsInternet))
            return WanVerdict.None;

        // Single destination: one internet endpoint dark while everything else is fine is nearly
        // always the endpoint's problem, and the flappy CDN endpoints would rebuild the noise
        // this alert class exists to remove. Visible on the charts, never notified.
        if (degraded.Count == 1)
            return WanVerdict.None;

        var branch = FindBranchLabel(targets, degraded);
        if (branch != null)
            return new WanVerdict(WanVerdictKind.Partial, false, null, null, branch,
                degraded.Count, targets.Count);

        // Independence gate, by network (ASN label / real ASN), inherited from the partial
        // detector: several endpoints of one provider are one network, not several.
        var networks = degraded.Select(NetworkKeyOf).Distinct().Count();
        if (networks >= 2)
            return new WanVerdict(WanVerdictKind.Partial, false, null, null, null,
                degraded.Count, targets.Count);

        // One network, no monitored shared ancestor: the network itself is the branch.
        return new WanVerdict(WanVerdictKind.Partial, false, null, null,
            NetworkKeyOf(degraded[0]), degraded.Count, targets.Count);
    }

    /// <summary>
    /// Break attribution for a total-with-first-hop-answering verdict, through
    /// <see cref="OutageDetector.AttributeBreak"/> with the current failing states as the
    /// cleanliness tests, so the alert path inherits its rules: only trace-map-anchored path
    /// hops may name where the break sat, and a clean row deeper than a failing one (a sibling
    /// branch) never anchors.
    /// </summary>
    private static (string? LastReachableHop, string? BrokenNetwork) AttributeUpstreamBreak(
        IReadOnlyList<WanTargetSnapshot> targets)
    {
        var failingByHop = new Dictionary<OutageDetector.Hop, bool>();
        foreach (var t in targets)
        {
            var hop = new OutageDetector.Hop(t.Name, t.Depth, Array.Empty<LatencySample>(),
                Groupable: false, AsnLabel: t.AsnLabel, IsGateway: false,
                KnownPosition: t.KnownPosition, IsInternet: t.IsInternet, AsnNumber: t.AsnNumber);
            failingByHop[hop] = t.Failing;
        }
        return OutageDetector.AttributeBreak(failingByHop.Keys,
            judged: _ => true,
            isClean: h => !failingByHop[h],
            isBroken: h => failingByHop[h]);
    }

    /// <summary>
    /// The shared-ancestor label for a branch-shaped partial, or null when the degraded set has
    /// no usable common ancestor. Two shapes count: a degraded path hop that every other
    /// degraded target sits behind (the branch head itself went dark), and a still-reachable
    /// hop that every degraded target - and no healthy one - sits behind (the break is just
    /// past it). Candidates must be trace-map-anchored path rows, never internet endpoints,
    /// mirroring the attribution rules.
    /// </summary>
    private static string? FindBranchLabel(
        IReadOnlyList<WanTargetSnapshot> targets, List<WanTargetSnapshot> degraded)
    {
        // A degraded path hop all other degraded targets are behind: the branch head. A healthy
        // target behind the same hop disqualifies it - the targets behind it must AGREE it is
        // gone, or the hop is just deprioritizing probes while forwarding fine.
        var head = degraded
            .Where(c => c.KnownPosition && !c.IsInternet
                && degraded.All(t => ReferenceEquals(t, c)
                    || t.AncestorIps.Contains(c.Address))
                && !targets.Any(t => !t.Degraded && t.AncestorIps.Contains(c.Address)))
            .OrderByDescending(c => c.Depth)
            .FirstOrDefault();
        if (head != null) return head.AsnLabel ?? head.Name;

        // A common ancestor of every degraded target that no healthy target is behind - an
        // ancestor healthy traffic also crosses (the access hop, usually) cannot be where the
        // break sits. Ancestors are raw hop IPs; only ones that map onto a trace-map-anchored
        // monitored path row can be named.
        var common = degraded
            .Select(t => (IEnumerable<string>)t.AncestorIps)
            .Aggregate((a, b) => a.Intersect(b, StringComparer.OrdinalIgnoreCase));
        var healthyAncestors = targets.Where(t => !t.Degraded)
            .SelectMany(t => t.AncestorIps)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byAddress = targets
            .Where(t => t.KnownPosition && !t.IsInternet)
            .GroupBy(t => t.Address, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var sharedAncestor = common
            .Where(ip => !healthyAncestors.Contains(ip) && byAddress.ContainsKey(ip))
            .Select(ip => byAddress[ip])
            .OrderByDescending(t => t.Depth)
            .FirstOrDefault();
        return sharedAncestor == null ? null : sharedAncestor.AsnLabel ?? sharedAncestor.Name;
    }

    /// <summary>Independence key, deferring to <see cref="OutageDetector.NetworkKey"/>.</summary>
    private static string NetworkKeyOf(WanTargetSnapshot t) =>
        OutageDetector.NetworkKey(new OutageDetector.Hop(t.Name, t.Depth,
            Array.Empty<LatencySample>(), AsnLabel: t.AsnLabel, AsnNumber: t.AsnNumber));
}
