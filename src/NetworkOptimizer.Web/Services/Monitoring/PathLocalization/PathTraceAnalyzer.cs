using System.Net;
using NetworkOptimizer.Monitoring.Probes;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;

namespace NetworkOptimizer.Web.Services.Monitoring.PathLocalization;

/// <summary>What the analysis says about one hop, beyond its numbers.</summary>
[Flags]
public enum HopTag
{
    None = 0,
    /// <summary>The shallowest hop of the elevated run that reaches the destination.</summary>
    Onset = 1,
    /// <summary>The last responding hop before the onset that stayed clean.</summary>
    LastClean = 2,
    /// <summary>Elevated, but the hops beyond did not carry it: the hop's own ICMP handling. Shown, never blamed.</summary>
    IcmpLimited = 4,
    /// <summary>A monitored hop whose own ping series decided it, not the trace.</summary>
    Anchored = 8,
    /// <summary>Lost the same number of probes every round: an ICMP rate limit, not forwarding loss.</summary>
    RateLimited = 16,
    /// <summary>Its RTT spread dwarfs the next hop's: slow-path ICMP generation.</summary>
    SlowPath = 32,
    /// <summary>Its reply came home by a hop count far from its forward position.</summary>
    ReturnDiffers = 64,
    /// <summary>The destination itself.</summary>
    Destination = 128,
    /// <summary>Part of the elevated run from the onset to the destination.</summary>
    InRun = 256,
    /// <summary>Elevated only by slowing in the same traces as the destination: moved with it, direction unknown.</summary>
    PairedOnly = 512
}

/// <summary>One TTL of a target's episode, pooled across its path rounds.</summary>
public sealed record HopReading
{
    public required int Ttl { get; init; }
    public string? Address { get; init; }
    public string? Hostname { get; init; }
    public int? Asn { get; init; }
    public string? AsnName { get; init; }
    public int Sent { get; init; }
    public int Responses { get; init; }
    public double LossPct => Sent == 0 ? 0 : 100.0 * (Sent - Responses) / Sent;
    /// <summary>The lowest loss of any answering hop after this one, the destination included; null when none answers.</summary>
    public double? DownstreamFloorPct { get; init; }
    /// <summary>
    /// The loss forwarding can account for. A forwarding loss here would carry to every hop after it,
    /// so anything above the least-lossy one beyond is this router limiting its own replies.
    /// </summary>
    public double EffectiveLossPct => DownstreamFloorPct is double f ? Math.Min(LossPct, f) : LossPct;
    /// <summary>The fastest round's min RTT: what a steady rise is judged on. An intermittent one always has a clean round, so pairing judges that.</summary>
    public double? MinRttMs { get; init; }
    /// <summary>Median of the rounds' average RTTs, for the slow-path tell.</summary>
    public double? MedianRttMs { get; init; }
    public double? MaxRttMs { get; init; }
    /// <summary>
    /// Each path round's min RTT at this TTL, in round order; null where the round had no answer here or
    /// another address answered. Min within a round leaves out a router's slow-path ICMP; across rounds
    /// the values are kept, so a rise that comes and goes is still seen.
    /// </summary>
    public IReadOnlyList<double?> RoundMinRttMs { get; init; } = Array.Empty<double?>();
    /// <summary>The median of <see cref="RoundMinRttMs"/>: the hop's RTT during the episode.</summary>
    public double? EpisodeRttMs { get; init; }
    /// <summary>The same hop's min RTT outside the episode: the recovery round, else the control round.</summary>
    public double? BaselineMinRttMs { get; init; }
    /// <summary>
    /// Best round against the baseline, which is one trace's min too: like for like. Never the median,
    /// which sits above a one-trace min on any jittery link (Starlink) with nothing wrong.
    /// </summary>
    public double? DeltaRttMs => MinRttMs.HasValue && BaselineMinRttMs.HasValue ? MinRttMs - BaselineMinRttMs : null;
    /// <summary>What a round is slower than: the hop's own second-fastest round in the episode (fastest under three).</summary>
    public double? ReferenceRttMs { get; init; }
    /// <summary>Per round, whether it was slower than <see cref="ReferenceRttMs"/> by the hop RTT threshold.</summary>
    public IReadOnlyList<bool> RoundSlow { get; init; } = Array.Empty<bool>();
    public int SlowRounds => RoundSlow.Count(s => s);
    /// <summary>Rounds slow here that were also slow at the destination; null on the destination itself.</summary>
    public int? PairedRounds { get; init; }
    /// <summary>
    /// The hop's own monitored series baseline (median RTT), for a monitored hop or destination. Display
    /// only: a median of burst averages is not comparable to a trace min, so no decision reads it.
    /// </summary>
    public double? SeriesBaselineRttMs { get; init; }
    /// <summary>Null when the hop said nothing (every probe unanswered).</summary>
    public bool? Elevated { get; init; }
    public HopTag Tags { get; init; }
    public int? ReturnHops { get; init; }
    public int? BaselineReturnHops { get; init; }

    public bool Has(HopTag tag) => (Tags & tag) == tag;
}

/// <summary>One target's traces, as the analyzer takes them.</summary>
/// <param name="TargetId">The target's TargetId.</param>
/// <param name="Name">Display name.</param>
/// <param name="Address">The address its probes go to (IP or hostname).</param>
/// <param name="PathRounds">Path rounds for a degraded target; the single control round for a control.</param>
/// <param name="Recovery">The recovery round, once it has run.</param>
public sealed record TargetTraces(string TargetId, string Name, string Address,
    IReadOnlyList<TracerouteResult> PathRounds, TracerouteResult? Recovery);

/// <summary>A degraded target's verdict and the hop table behind it.</summary>
public sealed record TargetVerdict
{
    public required string TargetId { get; init; }
    public required string Name { get; init; }
    public CongestionScope Scope { get; init; }
    public CongestionDisposition Disposition { get; init; }
    public string Label { get; init; } = "";
    public string? OnsetIp { get; init; }
    public int? OnsetTtl { get; init; }
    public string? LastCleanIp { get; init; }
    public bool PathChanged { get; init; }
    /// <summary>The elevation shows from an access-layer hop: the access network, not a path.</summary>
    public bool AccessLayer { get; init; }
    public ReturnPathFinding ReturnFinding { get; init; }
    public IReadOnlyList<HopReading> Hops { get; init; } = Array.Empty<HopReading>();
}

/// <summary>The episode's verdict: the targets' together, with the set analysis across them.</summary>
public sealed record EpisodeVerdict
{
    public CongestionScope Scope { get; init; } = CongestionScope.Unlocalized;
    public CongestionDisposition Disposition { get; init; } = CongestionDisposition.Unverifiable;
    public string? BottleneckHopIp { get; init; }
    public int? BottleneckHopNumber { get; init; }
    public string Label { get; init; } = "";
    public ReturnPathFinding ReturnFinding { get; init; }
    public string Summary { get; init; } = "";
    public IReadOnlyList<TargetVerdict> Targets { get; init; } = Array.Empty<TargetVerdict>();
}

/// <summary>
/// Localizes where a path-specific degradation first appears from the reactive traceroutes of an
/// episode (spec 5 and 6). Pure: everything it needs - the rounds, the ASN of each address, the verdict
/// of every hop that is also a monitored target - comes in as arguments.
///
/// The rule at its core is the localizer's, generalized from monitored hops to every hop of the trace
/// (<see cref="ElevatedRun"/>): an elevation is real only when every responding hop after it and the
/// destination carry it too. Anything else is the hop's own ICMP handling, shown and never blamed.
///
/// A traceroute sees the forward path only, and every hop's RTT includes that hop's own way home, so
/// the verdict says where the degradation first APPEARS, never which link broke. When every forward hop
/// is clean, or several destinations degrade together with nothing forward in common, it says so - the
/// return path or the far side - rather than pinning it on the last forward hop.
/// </summary>
public static class PathTraceAnalyzer
{
    /// <param name="degraded">The degraded targets' traces.</param>
    /// <param name="controls">The clean controls' rounds.</param>
    /// <param name="asnOf">ASN of an address, or null when unknown (private, unresolved).</param>
    /// <param name="anchorOf">For a hop that is also a monitored target: whether its own series was
    /// elevated over the episode. Null when it is not monitored or its series says nothing.</param>
    /// <param name="together">What the degraded targets did, for the sentence about several of them:
    /// "lost packets", "slowed down", or both. The caller knows from the targets' own series.</param>
    /// <param name="accessHops">Addresses of monitored access-layer hops (Access ISP, WAN L2 neighbor).</param>
    /// <param name="seriesBaselines">Monitored addresses' own series baseline RTT, for display.</param>
    public static EpisodeVerdict Analyze(
        IReadOnlyList<TargetTraces> degraded,
        IReadOnlyList<TargetTraces> controls,
        Func<string, AsnLookup?> asnOf,
        Func<string, bool?> anchorOf,
        IspHealthOptions options,
        string together = "degraded",
        IReadOnlySet<string>? accessHops = null,
        IReadOnlyDictionary<string, double>? seriesBaselines = null)
    {
        accessHops ??= new HashSet<string>();
        seriesBaselines ??= new Dictionary<string, double>();
        var controlHops = controls
            .SelectMany(c => c.PathRounds.SelectMany(r => r.Hops))
            .Where(h => h.Responded && h.RttMinMs.HasValue)
            .GroupBy(h => h.Address!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Min(h => h.RttMinMs!.Value), StringComparer.OrdinalIgnoreCase);

        // Which control crossed each hop, so a verdict can name the clean destination that went through it.
        var controlNameByHop = controls
            .SelectMany(c => c.PathRounds.SelectMany(r => r.Hops).Where(h => h.Responded).Select(h => (h.Address!, c.Name)))
            .GroupBy(x => x.Item1, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);

        var verdicts = degraded
            .Where(t => t.PathRounds.Count > 0)
            .Select(t => AnalyzeTarget(t, controlHops, controlNameByHop, asnOf, anchorOf, options, accessHops, seriesBaselines))
            .ToList();
        if (verdicts.Count == 0)
            return new EpisodeVerdict { Summary = "No trace has completed yet." };

        var controlNames = controls.Select(c => c.Name).ToList();
        var verdict = verdicts.Count == 1
            ? FromSingle(verdicts[0], controlNames)
            : FromSet(verdicts, controlNames, together);
        return verdict with { Summary = verdict.Summary + ReturnSentences(verdicts) };
    }

    /// <summary>
    /// " AWS us-east-1's replies came back over 15 hops during the problem and 12 after it." for each
    /// destination whose return hop count changed against the recovery round: the one view of the
    /// return path a forward trace gets.
    /// </summary>
    private static string ReturnSentences(IEnumerable<TargetVerdict> verdicts) => string.Concat(verdicts
        .Select(v => (v.Name, Dest: v.Hops.FirstOrDefault(h => h.Has(HopTag.Destination))))
        .Where(x => x.Dest is { ReturnHops: int d, BaselineReturnHops: int a } && d != a)
        .Select(x => $" {x.Name}'s replies came back over {x.Dest!.ReturnHops} hops during the problem and {x.Dest.BaselineReturnHops} after it."));

    /// <summary>"A", "A and B", "A, B and C".</summary>
    private static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}"
    };

    /// <summary>" Compared against AWS us-west-1, which stayed clean.", or nothing without a control.</summary>
    private static string ComparedAgainst(IReadOnlyList<string> controlNames) => controlNames.Count switch
    {
        0 => "",
        1 => $" Compared against {controlNames[0]}, which stayed clean.",
        _ => $" Compared against {JoinNames(controlNames)}, which stayed clean."
    };

    private static EpisodeVerdict FromSingle(TargetVerdict v, IReadOnlyList<string> controlNames)
    {
        var onset = v.Hops.FirstOrDefault(h => h.Has(HopTag.Onset) && !h.Has(HopTag.Destination));
        // A verdict the control decided (it crossed the onset clean) already names it.
        var summary = v.Scope == CongestionScope.Hop ? v.Label : v.Label + ComparedAgainst(controlNames);
        return new EpisodeVerdict
        {
            Scope = v.Scope,
            Disposition = v.Disposition,
            // The access verdict names its hop directly; it is not where the elevated run begins.
            BottleneckHopIp = v.AccessLayer ? v.OnsetIp : onset?.Address,
            BottleneckHopNumber = v.AccessLayer ? v.OnsetTtl : onset?.Ttl,
            Label = v.Label,
            ReturnFinding = v.ReturnFinding,
            Summary = summary,
            Targets = new[] { v }
        };
    }

    /// <summary>
    /// Several destinations degraded in one episode (spec 6.3). Each one's unexplained segment is its
    /// path past its last clean hop. Something they all share is the forward candidate; sharing nothing
    /// means the common factor is not on the forward path at all.
    /// </summary>
    private static EpisodeVerdict FromSet(List<TargetVerdict> verdicts, IReadOnlyList<string> controlNames, string together)
    {
        var names = JoinNames(verdicts.Select(v => v.Name).ToList());
        var sideBySide = controlNames.Count == 0 ? ""
            : $" {JoinNames(controlNames)} stayed clean meanwhile.";
        var returnFinding = verdicts.Select(v => v.ReturnFinding).FirstOrDefault(f => f != ReturnPathFinding.None);

        // The access network showing it on any path explains them all: every path crosses it.
        if (verdicts.FirstOrDefault(v => v.AccessLayer) is { } access)
        {
            return new EpisodeVerdict
            {
                Scope = CongestionScope.Hop,
                Disposition = CongestionDisposition.Confirmed,
                BottleneckHopIp = access.OnsetIp,
                BottleneckHopNumber = access.OnsetTtl,
                Label = access.Label,
                ReturnFinding = returnFinding,
                Summary = $"{names} {together} together. {access.Label}" + sideBySide,
                Targets = verdicts
            };
        }

        var segments = verdicts.Select(Unexplained).ToList();

        // A shared hop: the shallowest address every unexplained segment crosses.
        var sharedIps = segments
            .Select(s => s.Where(h => h.Address != null && !h.Has(HopTag.Destination)).Select(h => h.Address!))
            .Aggregate((a, b) => a.Intersect(b, StringComparer.OrdinalIgnoreCase).ToList())
            .ToList();
        if (sharedIps.Count > 0)
        {
            var hop = verdicts[0].Hops.Where(h => h.Address != null && sharedIps.Contains(h.Address, StringComparer.OrdinalIgnoreCase))
                .OrderBy(h => h.Ttl).First();
            var inRuns = verdicts.Select(v => v.Hops.FirstOrDefault(h => h.Has(HopTag.InRun)
                && string.Equals(h.Address, hop.Address, StringComparison.OrdinalIgnoreCase))).ToList();
            var inEveryRun = inRuns.All(h => h != null);
            // Placed by pairing on any path: the hops moved together, which way is not known.
            var pairedOnly = inEveryRun && inRuns.Any(h => h!.Has(HopTag.PairedOnly));
            var hopLabel = SharedHopSentence(names, hop, inEveryRun) + (pairedOnly ? ReturnPathCaveat : "");
            return new EpisodeVerdict
            {
                Scope = CongestionScope.Hop,
                Disposition = inEveryRun && !pairedOnly ? CongestionDisposition.Confirmed : CongestionDisposition.Unverifiable,
                BottleneckHopIp = hop.Address,
                BottleneckHopNumber = hop.Ttl,
                Label = hopLabel,
                ReturnFinding = returnFinding,
                Summary = hopLabel + sideBySide,
                Targets = verdicts
            };
        }

        // A shared network: an ASN every unexplained segment crosses before its destination. The
        // destinations' own ASN is the far side, never a forward confirmation.
        var destAsns = verdicts.Select(v => v.Hops.FirstOrDefault(h => h.Has(HopTag.Destination))?.Asn)
            .Where(a => a is > 0).Select(a => a!.Value).ToHashSet();
        var sharedAsns = segments
            .Select(s => s.Where(h => h.Asn is > 0).Select(h => h.Asn!.Value))
            .Aggregate((a, b) => a.Intersect(b).ToList())
            .ToList();
        if (sharedAsns.Count > 0)
        {
            var asn = sharedAsns[0];
            var sample = verdicts[0].Hops.First(h => h.Asn == asn);
            var network = NetworkName(sample);
            var farSide = destAsns.Count == 1 && destAsns.Contains(asn);
            bool InAsnRun(HopReading h) => h.Has(HopTag.InRun) && !h.Has(HopTag.Destination) && h.Asn == asn;
            var inEveryRun = !farSide && verdicts.All(v => v.Hops.Any(InAsnRun));
            // Placed by pairing on any path: every in-run hop of that network there moved with the destination only.
            var pairedOnly = inEveryRun && verdicts.Any(v => v.Hops.Where(InAsnRun).All(h => h.Has(HopTag.PairedOnly)));
            var asnLabel = farSide
                ? $"{names} all sit inside {network} and their routes share nothing before it, so the problem is inside {network} or on the return path."
                : inEveryRun
                    ? $"{names} all cross {network} after their last clean router, and every one shows the problem inside it." + (pairedOnly ? ReturnPathCaveat : "")
                    : $"{names} all cross {network} after their last clean router, but not every one shows the problem inside it.";
            return new EpisodeVerdict
            {
                Scope = CongestionScope.Asn,
                Disposition = inEveryRun && !pairedOnly ? CongestionDisposition.Confirmed : CongestionDisposition.Unverifiable,
                Label = asnLabel,
                ReturnFinding = returnFinding,
                Summary = asnLabel + sideBySide,
                Targets = verdicts
            };
        }

        // Nothing forward in common. The one segment every path shares is the access ISP, and on the way
        // in, its return path: named when the first public hop says which network that is.
        var lastCleans = verdicts.Select(v => v.Hops.LastOrDefault(h => h.Has(HopTag.LastClean))).ToList();
        var sharedLastClean = lastCleans.All(h => h?.Address != null && string.Equals(h.Address, lastCleans[0]!.Address, StringComparison.OrdinalIgnoreCase))
            ? $"after {HopName(lastCleans[0]!)}, the last router that stayed clean"
            : "after their last clean routers";
        var accessIsp = verdicts[0].Hops.FirstOrDefault(h => h.Asn is > 0 && !string.IsNullOrEmpty(h.AsnName))?.AsnName;
        var corridorLabel = $"{names} {together} together, but their routes share nothing {sharedLastClean}. "
            + $"The common factor is most likely the return path into {accessIsp ?? "your ISP"}, which a traceroute can't see.";
        return new EpisodeVerdict
        {
            Scope = CongestionScope.Corridor,
            Disposition = CongestionDisposition.Unverifiable,
            Label = corridorLabel,
            ReturnFinding = ReturnPathFinding.NoSharedForwardHop,
            Summary = corridorLabel + sideBySide,
            Targets = verdicts
        };
    }

    /// <summary>Appended where hops were placed by moving with the destination, which cannot tell direction.</summary>
    private const string ReturnPathCaveat = " Their replies may come home a different way from there, so it could be on the return path.";

    private static string SharedHopSentence(string names, HopReading hop, bool inEveryRun) => inEveryRun
        ? $"{names} all go through {HopName(hop)} after their last clean router, and every one shows the problem from there."
        : $"{names} all go through {HopName(hop)} after their last clean router, but not every one shows the problem from there.";

    /// <summary>A target's hops past its last clean one, through the destination.</summary>
    private static List<HopReading> Unexplained(TargetVerdict v)
    {
        var lastClean = v.Hops.LastOrDefault(h => h.Has(HopTag.LastClean));
        return v.Hops.Where(h => lastClean == null || h.Ttl > lastClean.Ttl).ToList();
    }

    /// <summary>Every IP address the analysis will ask the ASN of: each hop, each target, and what each
    /// hostname target resolved to.</summary>
    public static IEnumerable<string> AddressesOf(IEnumerable<TargetTraces> targets) => targets
        .SelectMany(t => t.PathRounds.Append(t.Recovery).Where(r => r != null)
            .SelectMany(r => r!.Hops.Select(h => h.Address).Append(r.ResolvedAddress))
            .Append(t.Address))
        .Where(a => a != null && IPAddress.TryParse(a, out _))
        .Select(a => a!)
        .Distinct(StringComparer.OrdinalIgnoreCase);

    internal static TargetVerdict AnalyzeTarget(
        TargetTraces t,
        IReadOnlyDictionary<string, double> controlMinRtt,
        IReadOnlyDictionary<string, string> controlNameByHop,
        Func<string, AsnLookup?> asnOf,
        Func<string, bool?> anchorOf,
        IspHealthOptions options,
        IReadOnlySet<string>? accessHops = null,
        IReadOnlyDictionary<string, double>? seriesBaselines = null)
    {
        seriesBaselines ??= new Dictionary<string, double>();
        var rounds = t.PathRounds;
        var destination = DestinationAddress(t);
        var destTtl = DestinationTtl(rounds, destination);

        var lastTtl = destTtl ?? rounds.SelectMany(r => r.Hops).Select(h => h.HopNumber).DefaultIfEmpty(0).Max();
        var recoveryByTtl = t.Recovery?.Hops.ToDictionary(h => h.HopNumber) ?? new Dictionary<int, TraceHop>();

        var hops = new List<HopReading>();
        for (var ttl = 1; ttl <= lastTtl; ttl++)
        {
            var perRound = rounds.Select(r => r.Hops.FirstOrDefault(h => h.HopNumber == ttl)).ToList();
            hops.Add(Pool(ttl, perRound, destTtl == ttl, recoveryByTtl, controlMinRtt, seriesBaselines, asnOf, options));
        }
        PairWithDestination(hops, options);
        if (destTtl == null)
        {
            // Never reached: the destination is still a monitored, degraded target, so it ends the
            // path as elevated on its own series' word.
            var destAsn = destination != null ? asnOf(destination) : null;
            hops.Add(new HopReading
            {
                Ttl = lastTtl + 1,
                Address = destination,
                Asn = destAsn?.Asn,
                AsnName = AsnLabel(destAsn),
                Tags = HopTag.Destination
            });
        }

        // Each hop's downstream floor, walking back from the end over the hops that answered.
        double? floor = null;
        for (var i = hops.Count - 1; i >= 0; i--)
        {
            hops[i] = hops[i] with { DownstreamFloorPct = floor };
            if (hops[i].Responses > 0)
                floor = floor is double f ? Math.Min(f, hops[i].LossPct) : hops[i].LossPct;
        }

        // Decide every forward hop. The destination is degraded by definition (it triggered the episode),
        // and its dense ping series outweighs a handful of trace probes.
        var decided = new bool?[hops.Count];
        var pairedOnly = new bool[hops.Count];
        var tags = new HopTag[hops.Count];
        var destSlow = hops.FirstOrDefault(h => h.Has(HopTag.Destination))?.SlowRounds ?? 0;
        // Pairing places the start only at a hop the trace decides, right after one that stayed fast in
        // every slow destination round. Without that clean point the swing is upstream of everything (a
        // Starlink handover moves every hop after the dish), so every hop pairs and none is the start.
        var firstPaired = destSlow >= options.PathHopMinPairedRounds
            ? hops.FindIndex(h => !h.Has(HopTag.Destination) && h.PairedRounds is int fp && fp * 2 > destSlow)
            : -1;
        var cleanBefore = firstPaired > 0 ? hops.Take(firstPaired).LastOrDefault(h => h.Responses > 0) : null;
        var pairingLocalizes = firstPaired >= 0 && hops[firstPaired].Address is string fa
            && anchorOf(fa) == null && accessHops?.Contains(fa) != true
            && cleanBefore is { PairedRounds: 0 };
        for (var i = 0; i < hops.Count; i++)
        {
            var h = hops[i];
            tags[i] = h.Tags;
            if (h.Has(HopTag.Destination)) { decided[i] = true; continue; }
            if (h.Responses == 0 || h.Address == null) { decided[i] = null; continue; }
            if (anchorOf(h.Address) is bool anchored)
            {
                decided[i] = anchored;
                tags[i] |= HopTag.Anchored;
                continue;
            }
            var lossArm = h.EffectiveLossPct >= options.PathHopLossElevatedPct && !h.Has(HopTag.RateLimited);
            var rttArm = h.DeltaRttMs is double d && h.BaselineMinRttMs is double b
                && d >= RttThreshold(b, options);
            // A rise that comes and goes: slow in most of the rounds the destination was slow in.
            var pairedArm = pairingLocalizes && i >= firstPaired && h.PairedRounds is int p && p * 2 > destSlow;
            decided[i] = lossArm || rttArm || pairedArm;
            pairedOnly[i] = pairedArm && !lossArm && !rttArm;
            if (pairedOnly[i]) tags[i] |= HopTag.PairedOnly;
        }

        // Slow-path tell: needs the next responding hop to compare against.
        for (var i = 0; i < hops.Count; i++)
        {
            var next = Enumerable.Range(i + 1, Math.Max(0, hops.Count - i - 1)).Select(j => hops[j])
                .FirstOrDefault(h => h.Responses > 0 && h.MaxRttMs.HasValue && h.MedianRttMs.HasValue);
            var h0 = hops[i];
            if (next == null || h0.MaxRttMs is not double max || h0.MedianRttMs is not double med) continue;
            var spread = max - med;
            var nextSpread = Math.Max(0.1, next.MaxRttMs!.Value - next.MedianRttMs!.Value);
            if (spread >= options.PathHopSlowPathMinSpreadMs && spread >= nextSpread * options.PathHopSlowPathSpreadFactor)
                tags[i] |= HopTag.SlowPath;
        }

        var onset = ElevatedRun.Onset(hops.Count, i => decided[i])!.Value;
        for (var i = 0; i < hops.Count; i++)
        {
            if (i >= onset && decided[i] == true) tags[i] |= HopTag.InRun;
            else if (decided[i] == true) tags[i] |= HopTag.IcmpLimited;
            // Absolved by the downstream floor: its own loss was over the line, forwarding's is not.
            else if (decided[i] == false && !tags[i].HasFlag(HopTag.Anchored)
                     && hops[i].LossPct >= options.PathHopLossElevatedPct && hops[i].EffectiveLossPct < options.PathHopLossElevatedPct)
                tags[i] |= HopTag.IcmpLimited;
        }
        tags[onset] |= HopTag.Onset;
        int? lastClean = null;
        for (var i = onset - 1; i >= 0; i--)
            if (decided[i] == false) { lastClean = i; break; }
        if (lastClean is int lc) tags[lastClean.Value] |= HopTag.LastClean;

        for (var i = 0; i < hops.Count; i++)
        {
            if (hops[i].ReturnHops is int rh && Math.Abs(rh - hops[i].Ttl) >= options.PathReturnAsymmetryHops)
                tags[i] |= HopTag.ReturnDiffers;
            hops[i] = hops[i] with { Elevated = decided[i], Tags = tags[i] };
        }

        var onsetHop = hops[onset];
        var lastCleanHop = lastClean is int c ? hops[c] : null;
        var returnFinding = ReturnFinding(hops, onset);

        // An access-layer hop whose own series is elevated: the problem is the access network, and a
        // hop past it reading clean against a control taken in the same slowdown says nothing.
        var accessHop = hops.FirstOrDefault(h => h.Address != null && accessHops?.Contains(h.Address) == true
            && h.Has(HopTag.Anchored) && h.Elevated == true);
        if (accessHop != null)
        {
            var isp = string.IsNullOrEmpty(accessHop.AsnName) ? "your ISP" : accessHop.AsnName;
            return new TargetVerdict
            {
                TargetId = t.TargetId,
                Name = t.Name,
                Scope = CongestionScope.Hop,
                Disposition = CongestionDisposition.Confirmed,
                Label = $"The problem shows from {isp}'s first hop, `{accessHop.Hostname ?? accessHop.Address}`: it is the access network, not a path.",
                OnsetIp = accessHop.Address,
                OnsetTtl = accessHop.Ttl,
                AccessLayer = true,
                ReturnFinding = returnFinding,
                Hops = hops
            };
        }

        var change = PathChange(rounds, t.Recovery, destination, asnOf);
        if (change is { } pc)
        {
            return new TargetVerdict
            {
                TargetId = t.TargetId,
                Name = t.Name,
                Scope = CongestionScope.Segment,
                Disposition = CongestionDisposition.Confirmed,
                Label = $"The route moved from {pc.From} to {pc.To} at hop {pc.Ttl} during the problem, and moved back after it.",
                OnsetIp = pc.Address,
                OnsetTtl = pc.Ttl,
                LastCleanIp = lastCleanHop?.Address,
                PathChanged = true,
                ReturnFinding = returnFinding,
                Hops = hops
            };
        }

        // The destination is degraded by definition, so it cannot confirm anything on its own: a hop
        // that rate-limits its own replies right before a silent stretch would always be the onset.
        // Confirmation needs a hop past the onset that answers and carries the elevation too.
        var corroborated = Enumerable.Range(onset + 1, Math.Max(0, hops.Count - onset - 1))
            .Any(j => decided[j] == true && !hops[j].Has(HopTag.Destination));

        // Every label is the sentence a user reads: a traceroute sees the forward path only, so the
        // wording says where the problem shows, and names the return path wherever it could be there.
        var showsThrough = $"Every router from there to {t.Name} shows it.";
        CongestionScope scope;
        CongestionDisposition disposition;
        string label;
        if (onsetHop.Has(HopTag.Destination))
        {
            scope = CongestionScope.Corridor;
            disposition = CongestionDisposition.Unverifiable;
            var farSide = string.IsNullOrEmpty(onsetHop.AsnName) ? t.Name : onsetHop.AsnName;
            label = lastCleanHop != null
                ? $"Every router on the way answered cleanly through {HopName(lastCleanHop)}, the last one that responds. The problem is past it: inside {farSide}, or on the return path, which a traceroute can't see."
                : $"No router on the way answered cleanly, so the problem could be anywhere up to {t.Name}, or on the return path.";
        }
        else if (onsetHop.Address != null && controlNameByHop.TryGetValue(onsetHop.Address, out var controlName))
        {
            // The control crossed this hop and stayed clean, so the hop itself carries traffic fine
            // and the elevation starts on its way toward the degraded destination.
            scope = CongestionScope.Hop;
            disposition = CongestionDisposition.Confirmed;
            label = $"The problem starts after {HopName(onsetHop)}: {controlName} goes through that router and stays clean.";
        }
        else if (lastCleanHop?.Asn is int a1 && onsetHop.Asn is int a2 && a1 != a2)
        {
            scope = CongestionScope.Segment;
            disposition = CongestionDisposition.Confirmed;
            label = $"The problem starts where {NetworkName(lastCleanHop)} hands off to {NetworkName(onsetHop)}. {showsThrough}";
        }
        else if (onsetHop.Asn is int asn && lastCleanHop?.Asn == asn)
        {
            scope = CongestionScope.Asn;
            disposition = CongestionDisposition.Confirmed;
            label = $"The problem starts inside {NetworkName(onsetHop)}, at `{onsetHop.Hostname ?? onsetHop.Address}`. {showsThrough}";
        }
        else
        {
            scope = CongestionScope.Segment;
            disposition = CongestionDisposition.Confirmed;
            label = lastCleanHop != null
                ? $"The problem starts between {HopName(lastCleanHop)} and {HopName(onsetHop)}. {showsThrough}"
                : $"The problem starts at {HopName(onsetHop)}. {showsThrough}";
        }

        // A clean control crossing the onset already confirmed it (the Hop branch above).
        if (disposition == CongestionDisposition.Confirmed && scope != CongestionScope.Hop && !corroborated)
        {
            disposition = CongestionDisposition.Unverifiable;
            label = $"The problem first shows at {HopName(onsetHop)}, but no router after it responds, so it could also be further along or on the return path.";
        }
        // Pairing shows the hops from the onset moved together, not which way: from there their replies
        // may come home by a congested return path the routers before it do not use.
        else if (pairedOnly[onset] && !onsetHop.Has(HopTag.Destination))
        {
            disposition = CongestionDisposition.Unverifiable;
            label = $"The problem first shows at {HopName(onsetHop)}, and every router after it shows it too. Their replies may come home a different way than the routers before it, so it could be on the return path.";
        }

        return new TargetVerdict
        {
            TargetId = t.TargetId,
            Name = t.Name,
            Scope = scope,
            Disposition = disposition,
            Label = label,
            OnsetIp = onsetHop.Address,
            OnsetTtl = onsetHop.Has(HopTag.Destination) ? null : onsetHop.Ttl,
            LastCleanIp = lastCleanHop?.Address,
            ReturnFinding = returnFinding,
            Hops = hops
        };
    }

    /// <summary>How far over a hop's reference RTT counts as slower: an absolute floor, or a fraction of the reference.</summary>
    private static double RttThreshold(double reference, IspHealthOptions options) =>
        Math.Max(options.PathHopRttMinDeltaMs, reference * options.PathHopRttDeltaFraction);

    /// <summary>
    /// Marks each hop's slow rounds against its reference, and counts the ones that line up with the
    /// destination's. A hop that slows in the same rounds as the destination is on the way to it; one
    /// that stays fast while the destination slows is not.
    /// </summary>
    private static void PairWithDestination(List<HopReading> hops, IspHealthOptions options)
    {
        for (var i = 0; i < hops.Count; i++)
        {
            var h = hops[i];
            // The hop's own rounds, never the recovery or control trace: that is one moment on another
            // clock, and on a jittery link it can sit above most rounds and hide which ones moved. The
            // second-fastest round from three on, so one lucky round does not make every other one slow.
            var answered = h.RoundMinRttMs.Where(v => v.HasValue).Select(v => v!.Value).OrderBy(v => v).ToList();
            if (answered.Count == 0) continue;
            var reference = answered.Count >= 3 ? answered[1] : answered[0];
            var threshold = RttThreshold(reference, options);
            hops[i] = h with
            {
                ReferenceRttMs = reference,
                RoundSlow = h.RoundMinRttMs.Select(v => v is double ms && ms - reference >= threshold).ToList()
            };
        }

        var dest = hops.FirstOrDefault(h => h.Has(HopTag.Destination));
        if (dest == null || dest.RoundSlow.Count == 0) return;
        for (var i = 0; i < hops.Count; i++)
        {
            if (hops[i].Has(HopTag.Destination)) continue;
            var slow = hops[i].RoundSlow;
            hops[i] = hops[i] with { PairedRounds = dest.RoundSlow.Where((s, r) => s && r < slow.Count && slow[r]).Count() };
        }
    }

    /// <summary>One TTL pooled across rounds: probes, responses, and RTTs summed or taken across them.</summary>
    /// <param name="perRound">The hop each path round recorded at this TTL, in round order; null where it recorded none.</param>
    private static HopReading Pool(int ttl, IReadOnlyList<TraceHop?> perRound, bool isDestination,
        IReadOnlyDictionary<int, TraceHop> recoveryByTtl, IReadOnlyDictionary<string, double> controlMinRtt,
        IReadOnlyDictionary<string, double> seriesBaselines, Func<string, AsnLookup?> asnOf, IspHealthOptions options)
    {
        var atTtl = perRound.Where(h => h != null).Select(h => h!).ToList();
        var address = atTtl.Where(h => h.Address != null)
            .GroupBy(h => h.Address!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        var same = atTtl.Where(h => h.Address == null || string.Equals(h.Address, address, StringComparison.OrdinalIgnoreCase)).ToList();
        var answered = same.Where(h => h.Responses > 0).ToList();
        var asn = address != null ? asnOf(address) : null;

        double? baseline = null;
        int? baseReturn = null;
        if (address != null && recoveryByTtl.TryGetValue(ttl, out var rec)
            && string.Equals(rec.Address, address, StringComparison.OrdinalIgnoreCase) && rec.RttMinMs.HasValue)
        {
            baseline = rec.RttMinMs;
            baseReturn = rec.ReturnHops;
        }
        else if (address != null && controlMinRtt.TryGetValue(address, out var ctl))
        {
            baseline = ctl;
        }

        var roundMins = perRound.Select(h => h is { Responses: > 0 } r && r.RttMinMs.HasValue
                && (r.Address == null || string.Equals(r.Address, address, StringComparison.OrdinalIgnoreCase))
            ? r.RttMinMs : null).ToList();

        var tags = isDestination ? HopTag.Destination : HopTag.None;
        // ICMP rate limit: the same number of probes lost in every round, never all of them.
        var lostPerRound = same.Where(h => h.Probes > 0).Select(h => h.Probes - h.Responses).ToList();
        if (lostPerRound.Count >= 3 && lostPerRound.Distinct().Count() == 1
            && lostPerRound[0] > 0 && same.All(h => h.Responses > 0))
            tags |= HopTag.RateLimited;

        return new HopReading
        {
            Ttl = ttl,
            Address = address,
            Hostname = answered.Select(h => h.Hostname).FirstOrDefault(n => !string.IsNullOrEmpty(n) && n != address),
            Asn = asn?.Asn,
            AsnName = AsnLabel(asn),
            Sent = same.Sum(h => Math.Max(h.Probes, h.Responses)),
            Responses = same.Sum(h => h.Responses),
            // Min within a round, not average: slow-path ICMP generation inflates a transit hop's average
            // and max far more than its minimum, which tracks queueing on the forwarding path (spec 5.1).
            // The episode reads the median of those (EpisodeRttMs): the best round alone hides a rise
            // that comes and goes. MinRttMs stays for the steady-rise arm, which compares like for like.
            MinRttMs = answered.Min(h => h.RttMinMs),
            RoundMinRttMs = roundMins,
            EpisodeRttMs = SeriesStats.Median(roundMins.Where(v => v.HasValue).Select(v => v!.Value).ToList()),
            MedianRttMs = SeriesStats.Median(answered.Where(h => h.RttAvgMs.HasValue).Select(h => h.RttAvgMs!.Value).ToList()),
            MaxRttMs = answered.Max(h => h.RttMaxMs),
            BaselineMinRttMs = baseline,
            SeriesBaselineRttMs = address != null && seriesBaselines.TryGetValue(address, out var series) ? series : null,
            ReturnHops = answered.Select(h => h.ReturnHops).LastOrDefault(r => r.HasValue),
            BaselineReturnHops = baseReturn,
            Tags = tags
        };
    }

    /// <summary>
    /// The return-path evidence (spec 6.1). A destination whose reply came home by a different number
    /// of hops during the episode than after it changed its return path, whatever the forward path did.
    /// </summary>
    private static ReturnPathFinding ReturnFinding(List<HopReading> hops, int onset)
    {
        var dest = hops.FirstOrDefault(h => h.Has(HopTag.Destination));
        if (dest is { ReturnHops: int during, BaselineReturnHops: int after } && during != after)
            return ReturnPathFinding.ReturnHopCountChanged;
        return hops.Skip(onset).Any(h => h.Has(HopTag.ReturnDiffers))
            ? ReturnPathFinding.AsymmetricHops
            : ReturnPathFinding.None;
    }

    private readonly record struct PathChangeAt(int Ttl, string? Address, string From, string To);

    /// <summary>
    /// Whether the forward path moved networks during the episode and moved back after it (spec 5.4),
    /// read against the recovery round. Until recovery has run there is nothing to compare with: the
    /// stored ancestry holds only the monitored hops, so every unmonitored transit on a real trace
    /// would read as a network the path moved into. An episode that never gets a recovery round (a
    /// metered WAN, or one that stopped short) is read for its onset alone, and its summary says why.
    ///
    /// A move is a swap, seen both ways: a network the episode's rounds crossed that recovery did not,
    /// AND one recovery crossed that no episode round did. Either alone is a hop that stopped
    /// answering ICMP, which drops its whole network out of one trace without the path going anywhere.
    /// Compared by ASN, since a different address inside one network is load balancing, not a move,
    /// and the destination's own network is left out: where a trace first sees it says nothing about
    /// the forward path.
    /// </summary>
    private static PathChangeAt? PathChange(IReadOnlyList<TracerouteResult> rounds, TracerouteResult? recovery,
        string? destination, Func<string, AsnLookup?> asnOf)
    {
        if (recovery == null) return null;
        var destAsn = destination != null ? asnOf(destination)?.Asn : null;
        List<(int Ttl, string Address, AsnLookup Asn)> Sequence(TracerouteResult r) =>
            AsnSequence(r.Hops.Where(h => h.Responded && !IsDestination(h.Address, destination)).Select(h => (h.HopNumber, h.Address!)), asnOf)
                .Where(x => x.Asn.Asn != destAsn).ToList();

        var after = Sequence(recovery);
        var during = rounds.Select(Sequence).ToList();
        var duringAsns = during.SelectMany(x => x).Select(x => x.Asn.Asn).ToHashSet();
        var afterAsns = after.Select(x => x.Asn.Asn).ToHashSet();

        var moved = during[^1].FirstOrDefault(d => !afterAsns.Contains(d.Asn.Asn));
        var left = after.FirstOrDefault(a => !duringAsns.Contains(a.Asn.Asn));
        if (moved.Address == null || left.Address == null) return null;
        return new PathChangeAt(moved.Ttl, moved.Address, NetworkName(left.Asn), NetworkName(moved.Asn));
    }

    /// <summary>The networks a path crosses in order, consecutive repeats collapsed, unknowns skipped.</summary>
    private static List<(int Ttl, string Address, AsnLookup Asn)> AsnSequence(IEnumerable<(int Ttl, string Address)> hops,
        Func<string, AsnLookup?> asnOf)
    {
        var seq = new List<(int, string, AsnLookup)>();
        foreach (var (ttl, address) in hops)
        {
            if (asnOf(address) is not { Asn: > 0 } asn) continue;
            if (seq.Count > 0 && seq[^1].Item3.Asn == asn.Asn) continue;
            seq.Add((ttl, address, asn));
        }
        return seq;
    }

    private static bool IsDestination(string? address, string? destination) =>
        address != null && destination != null && string.Equals(address, destination, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The destination's address: the target's own when it is an IP, else what the trace said the name
    /// resolved to. Null when no round said; the destination then ends the path on its own series'
    /// word, never on a guess at which hop it was.
    /// </summary>
    private static string? DestinationAddress(TargetTraces t) =>
        IPAddress.TryParse(t.Address, out _)
            ? t.Address
            : t.PathRounds.Select(r => r.ResolvedAddress).LastOrDefault(a => !string.IsNullOrEmpty(a));

    /// <summary>The TTL the destination answered at in most rounds, or null when no round reached it.</summary>
    private static int? DestinationTtl(IReadOnlyList<TracerouteResult> rounds, string? destination)
    {
        if (destination == null) return null;
        var ttls = rounds
            .Select(r => r.Hops.FirstOrDefault(h => string.Equals(h.Address, destination, StringComparison.OrdinalIgnoreCase)))
            .Where(h => h != null)
            .Select(h => h!.HopNumber)
            .ToList();
        if (ttls.Count > 0) return ttls.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
        return null;
    }

    /// <summary>
    /// "Lumen `edge6.Denver2` (`AS3356`)": the network, the hop's name or address, the ASN. Backticks
    /// render as code in the UI; the parentheses wrap only the ASN, so a sentence never nests them.
    /// </summary>
    internal static string HopName(HopReading h)
    {
        var name = $"`{h.Hostname ?? h.Address ?? $"hop {h.Ttl}"}`";
        return h.Asn is int asn
            ? (string.IsNullOrEmpty(h.AsnName) ? $"{name} ({AsnCode(asn)})" : $"{h.AsnName} {name} ({AsnCode(asn)})")
            : name;
    }

    /// <summary>"Akamai (`AS20940`)"; <paramref name="unknown"/>, else the hop's address, when its network is unknown.</summary>
    internal static string NetworkName(HopReading h, string? unknown = null) =>
        h.Asn is int asn
            ? (string.IsNullOrEmpty(h.AsnName) ? AsnCode(asn) : $"{h.AsnName} ({AsnCode(asn)})")
            : unknown ?? h.Address ?? $"hop {h.Ttl}";

    /// <summary>A hop table's Network cell, as Network Tools shows it: "Cox `AS22773`", with no parentheses.</summary>
    internal static string NetworkCell(HopReading h, string unknown = "-") =>
        h.Asn is int asn
            ? (string.IsNullOrEmpty(h.AsnName) ? AsnCode(asn) : $"{h.AsnName} {AsnCode(asn)}")
            : unknown;

    private static string NetworkName(AsnLookup a) =>
        AsnLabel(a) is { Length: > 0 } name ? $"{name} ({AsnCode(a.Asn)})" : AsnCode(a.Asn);

    /// <summary>
    /// The network's name as the rest of ISP Health shows it ("Example", not "EXAMPLE TELEPHONE CO"):
    /// the same cleaner the stored target names went through.
    /// </summary>
    internal static string? AsnLabel(AsnLookup? a) =>
        a == null || string.IsNullOrWhiteSpace(a.Name) ? null : NetworkOptimizer.Core.Helpers.NetworkFormatHelpers.CleanOrgName(a.Name);

    /// <summary>"`AS22773`": an AS number marked for code formatting.</summary>
    internal static string AsnCode(int asn) => $"`AS{asn}`";
}
