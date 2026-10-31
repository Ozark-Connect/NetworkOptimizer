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
    InRun = 256
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
    public double? MinRttMs { get; init; }
    public double? MedianRttMs { get; init; }
    public double? MaxRttMs { get; init; }
    /// <summary>The same hop's min RTT outside the episode: the recovery round, else the control round.</summary>
    public double? BaselineMinRttMs { get; init; }
    public double? DeltaRttMs => MinRttMs.HasValue && BaselineMinRttMs.HasValue ? MinRttMs - BaselineMinRttMs : null;
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
    public static EpisodeVerdict Analyze(
        IReadOnlyList<TargetTraces> degraded,
        IReadOnlyList<TargetTraces> controls,
        Func<string, AsnLookup?> asnOf,
        Func<string, bool?> anchorOf,
        IspHealthOptions options)
    {
        var controlHops = controls
            .SelectMany(c => c.PathRounds.SelectMany(r => r.Hops))
            .Where(h => h.Responded && h.RttMinMs.HasValue)
            .GroupBy(h => h.Address!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Min(h => h.RttMinMs!.Value), StringComparer.OrdinalIgnoreCase);

        var verdicts = degraded
            .Where(t => t.PathRounds.Count > 0)
            .Select(t => AnalyzeTarget(t, controlHops, asnOf, anchorOf, options))
            .ToList();
        if (verdicts.Count == 0)
            return new EpisodeVerdict { Summary = "No trace has completed yet." };

        var controlNames = controls.Select(c => c.Name).ToList();
        return verdicts.Count == 1
            ? FromSingle(verdicts[0], controlNames)
            : FromSet(verdicts, controlNames);
    }

    private static EpisodeVerdict FromSingle(TargetVerdict v, IReadOnlyList<string> controlNames)
    {
        var onset = v.Hops.FirstOrDefault(h => h.Has(HopTag.Onset) && !h.Has(HopTag.Destination));
        var summary = v.Disposition == CongestionDisposition.Confirmed
            ? $"{v.Name}: {v.Label}. Every responding hop past it and the destination carried it."
            : $"{v.Name}: {v.Label}. A forward traceroute cannot see the return path, so this is where it first appears, not a broken link.";
        if (controlNames.Count > 0) summary += $" Clean control: {string.Join(", ", controlNames)}.";
        return new EpisodeVerdict
        {
            Scope = v.Scope,
            Disposition = v.Disposition,
            BottleneckHopIp = onset?.Address,
            BottleneckHopNumber = onset?.Ttl,
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
    private static EpisodeVerdict FromSet(List<TargetVerdict> verdicts, IReadOnlyList<string> controlNames)
    {
        var names = string.Join(", ", verdicts.Select(v => v.Name));
        var sideBySide = controlNames.Count > 0
            ? $" Degraded: {names}. Clean: {string.Join(", ", controlNames)}."
            : $" Degraded: {names}.";
        var returnFinding = verdicts.Select(v => v.ReturnFinding).FirstOrDefault(f => f != ReturnPathFinding.None);

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
            var inEveryRun = verdicts.All(v => v.Hops.Any(h => h.Has(HopTag.InRun)
                && string.Equals(h.Address, hop.Address, StringComparison.OrdinalIgnoreCase)));
            return new EpisodeVerdict
            {
                Scope = CongestionScope.Hop,
                Disposition = inEveryRun ? CongestionDisposition.Confirmed : CongestionDisposition.Unverifiable,
                BottleneckHopIp = hop.Address,
                BottleneckHopNumber = hop.Ttl,
                Label = $"Shared by {verdicts.Count} degraded paths at {HopName(hop)}",
                ReturnFinding = returnFinding,
                Summary = (inEveryRun
                    ? $"The degraded destinations share {HopName(hop)} past their last clean hops, and every one carries the elevation from there."
                    : $"The degraded destinations share {HopName(hop)} past their last clean hops, but not every one carries the elevation from there.") + sideBySide,
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
            var inEveryRun = !farSide && verdicts.All(v => v.Hops.Any(h => h.Has(HopTag.InRun) && !h.Has(HopTag.Destination) && h.Asn == asn));
            return new EpisodeVerdict
            {
                Scope = CongestionScope.Asn,
                Disposition = inEveryRun ? CongestionDisposition.Confirmed : CongestionDisposition.Unverifiable,
                Label = farSide ? $"Inside {network}, the destinations' own network: the far side or the return path" : $"Inside {network}",
                ReturnFinding = returnFinding,
                Summary = (farSide
                    ? $"The degraded destinations all sit inside {network} and nothing forward of it is shared, so it is that network's far side or the return path."
                    : inEveryRun
                        ? $"The degraded destinations' paths share {network} past their last clean hops, and every one carries the elevation inside it."
                        : $"The degraded destinations' paths share {network} past their last clean hops, but not every one carries the elevation inside it.") + sideBySide,
                Targets = verdicts
            };
        }

        return new EpisodeVerdict
        {
            Scope = CongestionScope.Corridor,
            Disposition = CongestionDisposition.Unverifiable,
            Label = "Return path or far side: the degraded destinations share no forward hop",
            ReturnFinding = ReturnPathFinding.NoSharedForwardHop,
            Summary = "These destinations degraded together, yet their forward paths share nothing past their last clean hops, so the common factor is not on the forward path: most likely a shared return path into your ISP." + sideBySide,
            Targets = verdicts
        };
    }

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
        Func<string, AsnLookup?> asnOf,
        Func<string, bool?> anchorOf,
        IspHealthOptions options)
    {
        var rounds = t.PathRounds;
        var destination = DestinationAddress(t);
        var destTtl = DestinationTtl(rounds, destination);

        var lastTtl = destTtl ?? rounds.SelectMany(r => r.Hops).Select(h => h.HopNumber).DefaultIfEmpty(0).Max();
        var recoveryByTtl = t.Recovery?.Hops.ToDictionary(h => h.HopNumber) ?? new Dictionary<int, TraceHop>();

        var hops = new List<HopReading>();
        for (var ttl = 1; ttl <= lastTtl; ttl++)
        {
            var atTtl = rounds.Select(r => r.Hops.FirstOrDefault(h => h.HopNumber == ttl)).Where(h => h != null).Select(h => h!).ToList();
            hops.Add(Pool(ttl, atTtl, destTtl == ttl, recoveryByTtl, controlMinRtt, asnOf, options));
        }
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
                AsnName = destAsn?.Name,
                Tags = HopTag.Destination
            });
        }

        // Decide every forward hop. The destination is degraded by definition (it triggered the episode),
        // and its dense ping series outweighs a handful of trace probes.
        var decided = new bool?[hops.Count];
        var tags = new HopTag[hops.Count];
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
            var lossArm = h.LossPct >= options.PathHopLossElevatedPct && !h.Has(HopTag.RateLimited);
            var rttArm = h.DeltaRttMs is double d && h.BaselineMinRttMs is double b
                && d >= Math.Max(options.PathHopRttMinDeltaMs, b * options.PathHopRttDeltaFraction);
            decided[i] = lossArm || rttArm;
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

        var change = PathChange(rounds, t.Recovery, destination, asnOf);
        if (change is { } pc)
        {
            return new TargetVerdict
            {
                TargetId = t.TargetId,
                Name = t.Name,
                Scope = CongestionScope.Segment,
                Disposition = CongestionDisposition.Confirmed,
                Label = $"Path moved from {pc.From} to {pc.To} at hop {pc.Ttl}",
                OnsetIp = pc.Address,
                OnsetTtl = pc.Ttl,
                LastCleanIp = lastCleanHop?.Address,
                PathChanged = true,
                ReturnFinding = returnFinding,
                Hops = hops
            };
        }

        CongestionScope scope;
        CongestionDisposition disposition;
        string label;
        if (onsetHop.Has(HopTag.Destination))
        {
            scope = CongestionScope.Corridor;
            disposition = CongestionDisposition.Unverifiable;
            label = lastCleanHop != null
                ? $"Past the last clean hop ({HopName(lastCleanHop)}): the far side or the return path"
                : "No forward hop answered cleanly: the far side or the return path";
        }
        else if (onsetHop.Address != null && controlMinRtt.ContainsKey(onsetHop.Address))
        {
            // The control crossed this hop and stayed clean, so the hop itself carries traffic fine
            // and the elevation starts on its way toward the degraded destination.
            scope = CongestionScope.Hop;
            disposition = CongestionDisposition.Confirmed;
            label = $"Starts at {HopName(onsetHop)}";
        }
        else if (lastCleanHop?.Asn is int a1 && onsetHop.Asn is int a2 && a1 != a2)
        {
            scope = CongestionScope.Segment;
            disposition = CongestionDisposition.Confirmed;
            label = $"Between {NetworkName(lastCleanHop)} and {NetworkName(onsetHop)} (handoff)";
        }
        else if (onsetHop.Asn is int asn && lastCleanHop?.Asn == asn)
        {
            scope = CongestionScope.Asn;
            disposition = CongestionDisposition.Confirmed;
            label = $"Inside {NetworkName(onsetHop)}";
        }
        else
        {
            scope = CongestionScope.Segment;
            disposition = CongestionDisposition.Confirmed;
            label = lastCleanHop != null
                ? $"Between {HopName(lastCleanHop)} and {HopName(onsetHop)}"
                : $"Starts at {HopName(onsetHop)}";
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

    /// <summary>One TTL pooled across rounds: probes, responses, and RTTs summed or taken across them.</summary>
    private static HopReading Pool(int ttl, List<TraceHop> atTtl, bool isDestination,
        IReadOnlyDictionary<int, TraceHop> recoveryByTtl, IReadOnlyDictionary<string, double> controlMinRtt,
        Func<string, AsnLookup?> asnOf, IspHealthOptions options)
    {
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
            AsnName = asn?.Name,
            Sent = same.Sum(h => Math.Max(h.Probes, h.Responses)),
            Responses = same.Sum(h => h.Responses),
            // Min, not average: slow-path ICMP generation inflates a transit hop's average and max far
            // more than its minimum, which tracks queueing on the forwarding path (spec 5.1).
            MinRttMs = answered.Min(h => h.RttMinMs),
            MedianRttMs = SeriesStats.Median(answered.Where(h => h.RttAvgMs.HasValue).Select(h => h.RttAvgMs!.Value).ToList()),
            MaxRttMs = answered.Max(h => h.RttMaxMs),
            BaselineMinRttMs = baseline,
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

    /// <summary>"Lumen edge6.Denver2 (AS3356)": the network, the hop's name or address, the ASN.</summary>
    internal static string HopName(HopReading h)
    {
        var name = h.Hostname ?? h.Address ?? $"hop {h.Ttl}";
        return h.Asn is int asn
            ? (string.IsNullOrEmpty(h.AsnName) ? $"{name} (AS{asn})" : $"{h.AsnName} {name} (AS{asn})")
            : name;
    }

    /// <summary>"Akamai (AS20940)"; <paramref name="unknown"/>, else the hop's address, when its network is unknown.</summary>
    internal static string NetworkName(HopReading h, string? unknown = null) =>
        h.Asn is int asn
            ? (string.IsNullOrEmpty(h.AsnName) ? $"AS{asn}" : $"{h.AsnName} (AS{asn})")
            : unknown ?? h.Address ?? $"hop {h.Ttl}";

    private static string NetworkName(AsnLookup a) =>
        string.IsNullOrEmpty(a.Name) ? $"AS{a.Asn}" : $"{a.Name} (AS{a.Asn})";
}
