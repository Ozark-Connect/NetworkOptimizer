namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// The local access egress, the saved trace map's hop distances, and WAN load over time -
/// everything the localizer needs beyond the latency series themselves.
/// </summary>
public sealed class CongestionTopology
{
    /// <summary>IPs of the nearest public access hop(s). A bottleneck here under heavy WAN load, with nothing clean downstream, is self-inflicted bufferbloat.</summary>
    public HashSet<string> AccessEgressHopIps { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Hop distance (lowest TTL seen) per monitored hop IP, from Upstream Discovery. The ordering the bottleneck walk uses.</summary>
    public IReadOnlyDictionary<string, int> HopNumberByIp { get; init; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>WAN utilization (worst direction, fraction of expected plan speed) per sample. Utilization is null when expected speeds are unknown, leaving load-coincidence undetermined.</summary>
    public IReadOnlyList<(DateTime Time, double? Utilization)> Load { get; init; } = Array.Empty<(DateTime, double?)>();

    /// <summary>True when a trace map is persisted. When false the localizer can only report Unlocalized.</summary>
    public bool HasTraceMap { get; init; }
}

/// <summary>
/// Turns raw per-ASN congestion candidates into attributed events. Instead of merging
/// anything co-temporal into a "shared upstream" event, it localizes each elevation to the
/// bottleneck hop on the saved trace map, then classifies what it is: real congestion,
/// self-inflicted access bufferbloat under load, absolved control-plane (ICMP) noise, or
/// unverifiable for lack of downstream coverage.
///
/// The bottleneck of an elevated target is the shallowest hop in the unbroken run of elevated
/// hops ending at that target. A clean hop between two elevated ones breaks the run, so an
/// ICMP-deprioritized near hop (elevated but not forwarding-congested) is correctly excluded
/// from a deeper, real bottleneck and falls out as its own control-plane-noise event. This is
/// the transit farther-cluster absolve generalized to detection: an elevation that does not
/// propagate to proven-downstream hops is not congestion.
///
/// Attribution climbs a specificity ladder (<see cref="CongestionScope"/>) and reports the
/// highest tier the trace map and clean controls support, recording why it could not go deeper.
/// </summary>
public static class CongestionLocalizer
{
    public static List<CongestionEvent> Localize(
        IReadOnlyList<AsnSeries> allSeries,
        CongestionTopology topology,
        IspHealthOptions options)
    {
        var eventsBySeries = new Dictionary<AsnSeries, List<CongestionEvent>>();
        foreach (var s in allSeries)
            eventsBySeries[s] = CongestionDetector.DetectForSeries(s, options);

        // Destinations (CDN/anycast endpoints) are witnesses only - they confirm or refute
        // propagation past a hop, but an event is never *named* after a destination.
        var candidates = allSeries
            .Where(s => !s.IsDestination)
            .SelectMany(s => eventsBySeries[s].Select(e => (Series: s, Evt: e)))
            .ToList();
        if (candidates.Count == 0) return new List<CongestionEvent>();

        // A hop counts as elevated if it fired its own event OR shows in-window RTT excursions
        // above its baseline. The softer test matters for propagation: a downstream hop inherits
        // a real bottleneck's delay as spikes that are often too sparse to fire a sustained event,
        // and without it the localizer would wrongly absolve a genuine bottleneck as noise.
        bool IsElevated(AsnSeries s, DateTime start, DateTime end) =>
            (eventsBySeries.TryGetValue(s, out var evs) && evs.Any(e => Overlaps(e.Start, e.End, start, end)))
            || ElevatedInWindow(s, start, end, options);

        var anchored = allSeries.Where(s => HasTrace(s, topology)).ToList();

        var result = new List<CongestionEvent>();
        foreach (var cluster in ClusterByTime(candidates))
        {
            var window = (Start: cluster.Min(c => c.Evt.Start), End: cluster.Max(c => c.Evt.End));
            var elevatedSeries = cluster.Select(c => c.Series).Distinct().ToList();

            // Every monitored hop IP elevated somewhere in this window (own-hop level), so the
            // bottleneck walk knows which hops on a path are congested.
            var elevatedIps = new HashSet<string>(
                allSeries.Where(s => IsElevated(s, window.Start, window.End)).SelectMany(s => s.HopIps),
                StringComparer.OrdinalIgnoreCase);

            var byBottleneck = new Dictionary<string, List<AsnSeries>>(StringComparer.OrdinalIgnoreCase);
            var unanchored = new List<AsnSeries>();
            foreach (var s in elevatedSeries)
            {
                var bn = Bottleneck(s, elevatedIps, topology.HopNumberByIp);
                if (bn == null) { unanchored.Add(s); continue; }
                if (!byBottleneck.TryGetValue(bn, out var list)) byBottleneck[bn] = list = new List<AsnSeries>();
                list.Add(s);
            }

            var loadCoincident = LoadCoincident(window.Start, window.End, topology, options);
            var anchoredWithData = anchored.Where(s => HasDataInWindow(s, window.Start, window.End)).ToList();

            // Each anchored path's RTT rise over its OWN baseline. Under load every path picks up a
            // shared FLOOR of added delay; localized congestion is the rise ABOVE that floor.
            double RttRise(AsnSeries s)
            {
                var inW = s.Samples.Where(x => x.Time >= window.Start && x.Time <= window.End && x.RttAvgMs.HasValue)
                    .Select(x => x.RttAvgMs!.Value).ToList();
                var baseW = s.Samples.Where(x => (x.Time < window.Start || x.Time > window.End) && x.RttAvgMs.HasValue)
                    .Select(x => x.RttAvgMs!.Value).ToList();
                var im = SeriesStats.Median(inW);
                var bm = SeriesStats.Median(baseW);
                return im.HasValue && bm.HasValue ? Math.Max(0, im.Value - bm.Value) : 0;
            }
            var relElevated = anchoredWithData.Where(s => IsElevated(s, window.Start, window.End)).ToList();
            var rises = relElevated.Select(RttRise).Where(r => r > 0).OrderBy(r => r).ToList();
            var floorRise = SeriesStats.Median(rises) ?? 0;

            // A CLEAN control rose no more than the shared floor in RTT - it picked up the load floor
            // but NOT localized congestion. Using RTT (not the jitter-inclusive IsElevated) is the point:
            // under load the jitter floor lifts every path, so IsElevated would mark even Cox "elevated"
            // and erase every clean control - which is exactly what suppressed the "this hop's own
            // capacity, not your access egress" messaging. A non-zero count of clean parallel paths is
            // the proof an elevation is a single hop's capacity rather than access-layer bufferbloat.
            bool IsClean(AsnSeries s) => RttRise(s) <= floorRise * options.CongestionCleanControlFloorFactor;
            var cleanControlExists = anchoredWithData.Any(IsClean);

            // Self-inflicted access bufferbloat lifts (almost) every monitored path crossing the egress
            // under load. The robust median-shift test keys on "did everything drift up together": the
            // fraction of anchored paths (with data) whose in-window median rose above baseline.
            var lineWideUnderLoad = anchoredWithData.Count > 0
                && anchoredWithData.Count(s => RoseInWindow(s, window.Start, window.End, options))
                    >= anchoredWithData.Count * options.CongestionLineWideRiseFraction;

            // SHARED-INCIDENT COLLAPSE (narrow exception; the per-hop separation below is the default
            // and stays authoritative). Collapse to ONE event only when the rise is genuinely UNIFORM:
            // a strict majority rose in RTT (lineWideUnderLoad) AND no path rose MATERIALLY above the
            // shared floor. If a few paths are much worse than the floor, those are localized congestion
            // on top of the load - stay per-hop so the localizer surfaces them as "this hop's own
            // capacity", not "everything slowed".
            var uniform = rises.Count == 0 || floorRise <= 0
                || rises[^1] <= floorRise * options.CongestionLoadedUniformityFactor;
            if (lineWideUnderLoad && uniform && relElevated.Count > 0)
            {
                result.Add(BuildSharedIncident(relElevated, eventsBySeries, anchoredWithData, window,
                    topology, options, loadCoincident, IsElevated));
                continue;
            }

            foreach (var (bnIp, members) in byBottleneck)
                result.Add(BuildLocalized(bnIp, members, allSeries, eventsBySeries, window,
                    topology, options, loadCoincident, cleanControlExists, lineWideUnderLoad, uniform, IsElevated, IsClean));

            if (unanchored.Count > 0)
                result.Add(BuildUnlocalized(unanchored, eventsBySeries, window, topology, loadCoincident, options));
        }

        // An Unverifiable hop (dead-end, nothing monitored beyond it) inherits Confirmed from a
        // confirmed sibling on the SAME ASN overlapping in time: the same network degrading in the
        // same window is almost certainly the same incident, so the dead-end twin is real too.
        var confirmedEvents = result.Where(e => e.Disposition == CongestionDisposition.Confirmed).ToList();
        foreach (var evt in result)
        {
            if (evt.Disposition != CongestionDisposition.Unverifiable) continue;
            var sibling = confirmedEvents.FirstOrDefault(c =>
                c.AsnNumbers.Any(a => a > 0 && evt.AsnNumbers.Contains(a))
                && Overlaps(c.Start, c.End, evt.Start, evt.End));
            if (sibling == null) continue;
            evt.Disposition = CongestionDisposition.Confirmed;
            evt.ConfirmedBySibling = true;
            evt.Confidence = 70;
            evt.AttributionReason = "Confirmed by a sibling hop on the same network congesting in the same window; no monitored hop sits beyond this one to verify it directly.";
        }

        return result.OrderBy(e => e.Start).ToList();
    }

    /// <summary>
    /// Builds the single event for a shared incident (a relative-line-wide, access-rooted cluster).
    /// Attributed to the convergence hop nearest the user; Loaded Latency under load (your access
    /// link, suppressed), else real shared upstream congestion (Confirmed, scored). The span is
    /// trimmed to the sub-window where the breadth actually holds, so one hop lingering past the rest
    /// doesn't stretch it, and the worst single hop is named in the reason.
    /// </summary>
    private static CongestionEvent BuildSharedIncident(
        List<AsnSeries> elevated,
        Dictionary<AsnSeries, List<CongestionEvent>> eventsBySeries,
        List<AsnSeries> anchoredWithData,
        (DateTime Start, DateTime End) window,
        CongestionTopology topology,
        IspHealthOptions options,
        bool loadCoincident,
        Func<AsnSeries, DateTime, DateTime, bool> isElevated)
    {
        int HopNum(AsnSeries s) => s.HopIps.Concat(s.AncestorIps)
            .Where(topology.HopNumberByIp.ContainsKey)
            .Select(ip => topology.HopNumberByIp[ip]).DefaultIfEmpty(int.MaxValue).Min();

        // Decision 3: span only the contiguous sub-window where the breadth still holds, so a single
        // hop lingering past the rest cannot stretch the incident's reported (and scored) duration.
        var bucket = TimeSpan.FromMinutes(options.CongestionBucketMinutes);
        var need = anchoredWithData.Count * options.CongestionLineWideRiseFraction;
        var wide = new List<DateTime>();
        for (var b = CongestionDetector.FloorTime(window.Start, bucket); b < window.End; b += bucket)
            if (anchoredWithData.Count(s => isElevated(s, b, b + bucket)) >= need)
                wide.Add(b);
        var start = wide.Count > 0 ? wide.Min() : window.Start;
        var end = wide.Count > 0 ? wide.Max() + bucket : window.End;

        Func<AsnSeries, IEnumerable<CongestionEvent>> evts = s =>
            (eventsBySeries.TryGetValue(s, out var es) ? es : Enumerable.Empty<CongestionEvent>())
                .Where(e => Overlaps(e.Start, e.End, start, end));

        // The convergence hop nearest the user owns the event (usually the access egress); this also
        // keeps the score attributed to your ISP card rather than every downstream transit victim.
        var owner = elevated.OrderBy(HopNum).First();
        var ownerEvt = evts(owner).FirstOrDefault();

        // Owner magnitudes from its fired event when it has one; otherwise (the owner was elevated via
        // the relative jitter/excursion arm with no fired RTT event) compute them from its samples, so
        // the row never reports 0 -> 0.
        double baseRtt, peakRtt, baseJit, peakJit;
        if (ownerEvt != null)
        {
            baseRtt = ownerEvt.BaselineRttMs;
            peakRtt = ownerEvt.PeakRttMs;
            baseJit = ownerEvt.BaselineJitterMs;
            peakJit = ownerEvt.PeakJitterMs;
        }
        else
        {
            var inR = owner.Samples.Where(x => x.Time >= start && x.Time <= end && x.RttAvgMs.HasValue).Select(x => x.RttAvgMs!.Value).ToList();
            var baseR = owner.Samples.Where(x => (x.Time < start || x.Time > end) && x.RttAvgMs.HasValue).Select(x => x.RttAvgMs!.Value).ToList();
            var inJ = owner.Samples.Where(x => x.Time >= start && x.Time <= end).Select(x => x.EffectiveJitterMs).Where(j => j.HasValue).Select(j => j!.Value).ToList();
            var baseJ = owner.Samples.Where(x => x.Time < start || x.Time > end).Select(x => x.EffectiveJitterMs).Where(j => j.HasValue).Select(j => j!.Value).ToList();
            baseRtt = SeriesStats.Median(baseR) ?? 0;
            peakRtt = inR.Count > 0 ? SeriesStats.Percentile(inR, 0.90) ?? baseRtt : baseRtt;
            baseJit = SeriesStats.Median(baseJ) ?? 0;
            peakJit = inJ.Count > 0 ? inJ.Max() : baseJit;
        }

        // Decision 2: name the single worst hop (largest relative RTT rise among the fired members).
        var worst = elevated
            .SelectMany(s => evts(s).Select(e => (Series: s, Rise: e.BaselineRttMs > 0 ? (e.PeakRttMs - e.BaselineRttMs) / e.BaselineRttMs : 0)))
            .OrderByDescending(x => x.Rise)
            .Select(x => x.Series)
            .FirstOrDefault();

        var reason = loadCoincident
            ? $"{elevated.Count} monitored paths rose together under heavy WAN load, so the limit was your access link (bufferbloat or a congested shared-access network), not one hop."
            : $"{elevated.Count} monitored paths across independent networks rose together with no heavy local load - a shared upstream bottleneck lifting the whole path, not one hop.";
        if (worst != null && worst != owner && worst.AsnName is { Length: > 0 } worstName)
            reason += $" Worst at {worstName}.";

        return new CongestionEvent
        {
            Start = start,
            End = end,
            AsnNumbers = owner.AsnNumber > 0 ? new List<int> { owner.AsnNumber } : new List<int>(),
            AsnNames = owner.AsnName is { Length: > 0 } ? new List<string> { owner.AsnName } : new List<string>(),
            TargetIds = owner.TargetIds.ToList(),
            BaselineRttMs = baseRtt,
            PeakRttMs = peakRtt,
            BaselineJitterMs = baseJit,
            PeakJitterMs = peakJit,
            Scope = CongestionScope.Unlocalized,
            Disposition = loadCoincident ? CongestionDisposition.SelfInflicted : CongestionDisposition.Confirmed,
            BottleneckHopIp = owner.HopIps.FirstOrDefault(),
            BottleneckLabel = owner.AsnName,
            LoadCoincident = loadCoincident,
            Confidence = 70,
            AttributionReason = reason
        };
    }

    /// <summary>
    /// The shallowest hop in the unbroken elevated run ending at <paramref name="series"/>. A
    /// clean hop between two elevated ones terminates the run, so the bottleneck never jumps
    /// across a clean middle hop to a shallower (noisy) one.
    /// </summary>
    private static string? Bottleneck(AsnSeries series, HashSet<string> elevatedIps,
        IReadOnlyDictionary<string, int> hopNumberByIp)
    {
        var path = series.HopIps.Concat(series.AncestorIps)
            .Where(hopNumberByIp.ContainsKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ip => hopNumberByIp[ip])
            .ToList();
        if (path.Count == 0) return null;

        string? bottleneck = null;
        for (var i = path.Count - 1; i >= 0; i--)
        {
            if (elevatedIps.Contains(path[i])) bottleneck = path[i];
            else break;
        }
        return bottleneck;
    }

    private static CongestionEvent BuildLocalized(
        string bottleneckIp,
        List<AsnSeries> members,
        IReadOnlyList<AsnSeries> allSeries,
        Dictionary<AsnSeries, List<CongestionEvent>> eventsBySeries,
        (DateTime Start, DateTime End) window,
        CongestionTopology topology,
        IspHealthOptions options,
        bool loadCoincident,
        bool cleanControlExists,
        bool lineWideUnderLoad,
        bool uniform,
        Func<AsnSeries, DateTime, DateTime, bool> isElevated,
        Func<AsnSeries, bool> isClean)
    {
        var hopNum = topology.HopNumberByIp.TryGetValue(bottleneckIp, out var hn) ? hn : int.MaxValue;
        // The hop the congestion sits ON owns the event - that ASN is the culprit, the deeper
        // members are downstream victims and must not be penalized for it. Prefer a real hop
        // (non-destination) bearing this IP.
        var bottleneckSeries =
            allSeries.FirstOrDefault(s => !s.IsDestination && s.HopIps.Contains(bottleneckIp, StringComparer.OrdinalIgnoreCase))
            ?? allSeries.FirstOrDefault(s => s.HopIps.Contains(bottleneckIp, StringComparer.OrdinalIgnoreCase));

        // Monitored targets that provably route THROUGH the bottleneck (it is in their ancestry).
        var descendants = allSeries
            .Where(s => s.AncestorIps.Contains(bottleneckIp, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var hasDescendant = descendants.Count > 0;
        // Propagation = the elevation reaches the FIRST monitored hop past the bottleneck. A
        // deeper elevated member of this same group counts; otherwise the nearest downstream
        // witness must be elevated. A clean immediate-downstream hop means the elevation did
        // not forward through - control-plane noise, not congestion (the transit
        // farther-cluster absolve, generalized).
        var immediateHop = descendants
            .Select(d => DeepestHopNum(d, topology))
            .Where(h => h > hopNum)
            .DefaultIfEmpty(int.MaxValue)
            .Min();
        var propagated =
            members.Any(m => DeepestHopNum(m, topology) > hopNum)
            || descendants.Any(d => DeepestHopNum(d, topology) == immediateHop
                && isElevated(d, window.Start, window.End));

        var isAccessEgress = topology.AccessEgressHopIps.Contains(bottleneckIp);

        // Anchored paths that do NOT cross this bottleneck and stayed clean through the window.
        // A non-zero count under load proves the elevation is this hop's own capacity, not
        // access-layer bufferbloat (which would lift every path that shares your access link).
        // Require samples IN the window: a series with no data then (a newer target added after a
        // past event, or a monitoring gap) is unknown, not clean, and must not pad this evidence.
        // Clean = rose no more than the shared load floor in RTT (isClean), NOT merely "not elevated":
        // under load the jitter floor lifts every path, so a jitter-inclusive test would count zero
        // clean controls and wrongly drop the "this hop's own capacity, not your access egress" finding.
        var cleanParallelPaths = allSeries.Count(s => HasTrace(s, topology)
            && HasDataInWindow(s, window.Start, window.End)
            && !s.HopIps.Contains(bottleneckIp, StringComparer.OrdinalIgnoreCase)
            && !s.AncestorIps.Contains(bottleneckIp, StringComparer.OrdinalIgnoreCase)
            && isClean(s));

        CongestionDisposition disposition;
        string reason;
        if (isAccessEgress && loadCoincident && lineWideUnderLoad && uniform)
        {
            // Bottleneck is the access egress AND every monitored path drifted up TOGETHER and UNIFORMLY
            // under load (line-wide, no path materially worse than the shared floor). That's loaded
            // latency where all your traffic converges, not a distinct ISP hop. The uniform gate mirrors
            // the collapse: a shared floor plus a few materially-worse hops is localized congestion on
            // top of load, not bufferbloat, so it must NOT read as self-inflicted here either. Surfaced
            // as "Loaded Latency" (not Congestion), not scored (Suppressed). We assert location +
            // correlation only; the mechanism (CPE buffer, OLT, policing) is unknowable here.
            disposition = CongestionDisposition.SelfInflicted;
            reason = "Every monitored path rose together under load, so the limit was your access link, not a single hop - consistent with bufferbloat or a congested shared-access network.";
        }
        else if (propagated)
        {
            disposition = CongestionDisposition.Confirmed;
            reason = "Elevation propagates to monitored hops downstream of this bottleneck.";
            if (loadCoincident)
                reason += cleanParallelPaths > 0
                    ? $" It coincided with heavy WAN load, but {cleanParallelPaths} other monitored paths stayed clean under the same load, so it was this hop's own capacity, not your access egress."
                    : " It coincided with heavy WAN load (load-induced), but localizes to a specific hop rather than your access egress.";
        }
        else if (hasDescendant)
        {
            disposition = CongestionDisposition.ControlPlaneNoise;
            reason = "Elevation does not reach monitored hops downstream of this one - control-plane (ICMP) deprioritization at the hop, not a forwarding bottleneck.";
        }
        else
        {
            disposition = CongestionDisposition.Unverifiable;
            reason = "No monitored target sits downstream of this hop, so forwarding congestion cannot be confirmed. Add a probe past it to verify.";
        }

        var label = bottleneckSeries?.AsnName
            ?? (bottleneckSeries != null ? $"AS{bottleneckSeries.AsnNumber}" : bottleneckIp);

        var confidence = disposition switch
        {
            CongestionDisposition.SelfInflicted => 80,
            CongestionDisposition.Confirmed => cleanControlExists ? 85 : 70,
            CongestionDisposition.ControlPlaneNoise => 75,
            CongestionDisposition.Unverifiable => 35,
            _ => 50
        };

        var evt = NewEvent(bottleneckSeries, members, eventsBySeries, window, options);
        evt.Scope = CongestionScope.Hop;
        evt.Disposition = disposition;
        evt.BottleneckHopIp = bottleneckIp;
        evt.BottleneckLabel = label;
        evt.LoadCoincident = loadCoincident;
        evt.CleanParallelPaths = cleanParallelPaths;
        evt.Confidence = confidence;
        evt.AttributionReason = reason;
        return evt;
    }

    private static CongestionEvent BuildUnlocalized(
        List<AsnSeries> members,
        Dictionary<AsnSeries, List<CongestionEvent>> eventsBySeries,
        (DateTime Start, DateTime End) window,
        CongestionTopology topology,
        bool loadCoincident,
        IspHealthOptions options)
    {
        var evt = NewEvent(null, members, eventsBySeries, window, options);
        evt.Scope = CongestionScope.Unlocalized;
        evt.Disposition = CongestionDisposition.Confirmed;
        evt.LoadCoincident = loadCoincident;
        evt.Confidence = 25;
        evt.AttributionReason = topology.HasTraceMap
            ? "These targets are not on the trace map, so the bottleneck cannot be placed. Re-run Upstream Discovery to localize."
            : "No trace map is saved, so congestion cannot be localized. Run Upstream Discovery to enable hop attribution.";
        return evt;
    }

    private static CongestionEvent NewEvent(
        AsnSeries? identity,
        List<AsnSeries> members,
        Dictionary<AsnSeries, List<CongestionEvent>> eventsBySeries,
        (DateTime Start, DateTime End) window,
        IspHealthOptions options)
    {
        // Metrics come from the culprit hop when it has its own elevation, else the worst member.
        var metricSource = identity != null
            && eventsBySeries.TryGetValue(identity, out var idEvents)
            && idEvents.Any(e => Overlaps(e.Start, e.End, window.Start, window.End))
            ? new List<AsnSeries> { identity }
            : members;
        var sourceEvents = metricSource
            .SelectMany(m => eventsBySeries[m].Where(e => Overlaps(e.Start, e.End, window.Start, window.End)))
            .ToList();
        var worst = sourceEvents.OrderByDescending(e => e.PeakRttMs - e.BaselineRttMs).First();
        // The penalized ASN/targets are the bottleneck hop when localized; downstream victims
        // are excluded. An unlocalized event has no single culprit, so it keeps the full set.
        var id = identity != null ? new List<AsnSeries> { identity } : members;
        // Report the shared cluster window when this bottleneck's own elevation covers most of it,
        // so a genuine co-temporal event reads as one clean window across its per-hop rows. Only a
        // member that cleared much earlier than the others (an outlier) reports its own shorter span
        // - so a hop that resolved in 45 min isn't stamped with a co-occurring hop's multi-hour span.
        // Correlation/propagation/disposition above still use the full cluster window regardless.
        var ownStart = sourceEvents.Min(e => e.Start);
        var ownEnd = sourceEvents.Max(e => e.End);
        var clusterSpan = (window.End - window.Start).TotalSeconds;
        var sharesWindow = clusterSpan <= 0
            || (ownEnd - ownStart).TotalSeconds >= clusterSpan * options.CongestionSharedWindowMinFraction;
        return new CongestionEvent
        {
            Start = sharesWindow ? window.Start : ownStart,
            End = sharesWindow ? window.End : ownEnd,
            AsnNumbers = id.Select(m => m.AsnNumber).Distinct().ToList(),
            AsnNames = id.Select(m => m.AsnName ?? $"AS{m.AsnNumber}").Distinct().ToList(),
            TargetIds = id.SelectMany(m => m.TargetIds).Distinct().ToList(),
            BaselineRttMs = worst.BaselineRttMs,
            PeakRttMs = worst.PeakRttMs,
            BaselineJitterMs = worst.BaselineJitterMs,
            PeakJitterMs = worst.PeakJitterMs
        };
    }

    private static bool LoadCoincident(DateTime start, DateTime end, CongestionTopology topology, IspHealthOptions options)
    {
        var inWindow = topology.Load
            .Where(l => l.Time >= start && l.Time <= end && l.Utilization.HasValue)
            .ToList();
        if (inWindow.Count == 0) return false; // unknown load -> never claim self-inflicted
        var high = inWindow.Count(l => l.Utilization!.Value >= options.CongestionLoadHighFraction);
        return (double)high / inWindow.Count >= options.CongestionLoadCoincidenceFraction;
    }

    /// <summary>
    /// A softer "elevated in this window" test than firing a full congestion event, used for the
    /// propagation and clean-control checks so the localizer doesn't absolve a genuine bottleneck as
    /// control-plane noise just because nothing downstream fired its own event. A hop counts as
    /// elevated if EITHER its RTT rose (a meaningful fraction of in-window RTT samples exceed its own
    /// baseline p90 by the congestion RTT floor) OR its jitter rose relative to its own baseline.
    /// The jitter arm matters because many incidents are jitter-driven with flat RTT - an RTT-only
    /// test reads the downstream as clean and shatters one chain-wide rise into per-hop noise rows.
    /// Both arms are relative to the hop's OWN baseline; clean off-path hops sit near zero on both.
    /// </summary>
    internal static bool ElevatedInWindow(AsnSeries series, DateTime start, DateTime end, IspHealthOptions options)
    {
        var inWindow = series.Samples
            .Where(x => x.Time >= start && x.Time <= end && x.RttAvgMs.HasValue)
            .Select(x => x.RttAvgMs!.Value).ToList();
        var baseline = series.Samples
            .Where(x => (x.Time < start || x.Time > end) && x.RttAvgMs.HasValue)
            .Select(x => x.RttAvgMs!.Value).ToList();
        if (inWindow.Count == 0 || baseline.Count == 0) return false;

        var baselineP90 = SeriesStats.Percentile(baseline, 0.90);
        if (baselineP90.HasValue)
        {
            var threshold = baselineP90.Value + options.CongestionRttMinDeltaMs;
            var excursionFraction = inWindow.Count(v => v > threshold) / (double)inWindow.Count;
            if (excursionFraction >= options.CongestionPropagationExcursionFraction) return true;
        }

        // Jitter arm: the downstream of a real bottleneck often inherits the delay as jitter while its
        // median RTT barely moves, so RTT-only propagation misses it. Compare in-window median jitter
        // to this hop's OWN baseline median (relative, with a small absolute floor so a near-zero hop
        // isn't tripped by a sub-ms ratio swing).
        var inJitter = series.Samples
            .Where(x => x.Time >= start && x.Time <= end).Select(x => x.EffectiveJitterMs)
            .Where(j => j.HasValue).Select(j => j!.Value).ToList();
        var baseJitter = series.Samples
            .Where(x => x.Time < start || x.Time > end).Select(x => x.EffectiveJitterMs)
            .Where(j => j.HasValue).Select(j => j!.Value).ToList();
        if (inJitter.Count == 0 || baseJitter.Count == 0) return false;

        var inJitMedian = SeriesStats.Median(inJitter);
        var baseJitMedian = SeriesStats.Median(baseJitter);
        return inJitMedian.HasValue && baseJitMedian.HasValue
            && inJitMedian.Value >= baseJitMedian.Value * options.CongestionPropagationJitterFactor
            && inJitMedian.Value - baseJitMedian.Value >= options.CongestionPropagationJitterFloorMs;
    }

    private static int DeepestHopNum(AsnSeries series, CongestionTopology topology) => series.HopIps
        .Select(ip => topology.HopNumberByIp.TryGetValue(ip, out var n) ? n : int.MinValue)
        .DefaultIfEmpty(int.MinValue)
        .Max();

    private static bool HasTrace(AsnSeries series, CongestionTopology topology) =>
        series.AncestorIps.Count > 0 || series.HopIps.Any(topology.HopNumberByIp.ContainsKey);

    /// <summary>True when the series has at least one RTT sample inside the window. A series with no
    /// data then (a newer target added after a past event, or a monitoring gap) reads as unknown,
    /// never as "clean" - so absence of data can't pad the clean-control or clean-parallel evidence.</summary>
    private static bool HasDataInWindow(AsnSeries series, DateTime start, DateTime end) =>
        series.Samples.Any(x => x.Time >= start && x.Time <= end && x.RttAvgMs.HasValue);

    /// <summary>
    /// True when the series' in-window median rose above its out-of-window baseline median by at least
    /// CongestionLineWideMinShiftMs. The robust per-path "drifted up under load" signal: unlike the
    /// absolute elevation bar it isn't fooled by a high-variance path's inflated p90, so a constant
    /// bufferbloat offset registers even on high-baseline hops. Used only for the line-wide test.
    /// </summary>
    private static bool RoseInWindow(AsnSeries series, DateTime start, DateTime end, IspHealthOptions options)
    {
        var inWindow = series.Samples
            .Where(x => x.Time >= start && x.Time <= end && x.RttAvgMs.HasValue)
            .Select(x => x.RttAvgMs!.Value).ToList();
        var baseline = series.Samples
            .Where(x => (x.Time < start || x.Time > end) && x.RttAvgMs.HasValue)
            .Select(x => x.RttAvgMs!.Value).ToList();
        if (inWindow.Count == 0 || baseline.Count == 0) return false;
        var bMed = SeriesStats.Median(baseline);
        // Use a high in-window percentile, not the median: an event with a strong core and a long
        // mild tail dilutes the median toward baseline, so a path that genuinely rose for a good part
        // of the window flickers in and out of "rose" across recomputes and the line-wide breadth
        // hovers at its threshold. The percentile captures the strong portion robustly; a flat path
        // (or one that only nudged the tail) still sits at baseline and does not count.
        var eHigh = SeriesStats.Percentile(inWindow, options.CongestionLineWideRisePercentile);
        return bMed.HasValue && eHigh.HasValue && eHigh.Value > bMed.Value + options.CongestionLineWideMinShiftMs;
    }

    private static List<List<(AsnSeries Series, CongestionEvent Evt)>> ClusterByTime(
        List<(AsnSeries Series, CongestionEvent Evt)> candidates)
    {
        var clusters = new List<List<(AsnSeries, CongestionEvent)>>();
        List<(AsnSeries, CongestionEvent)>? current = null;
        var currentEnd = DateTime.MinValue;
        foreach (var c in candidates.OrderBy(c => c.Evt.Start))
        {
            if (current == null || c.Evt.Start > currentEnd)
            {
                current = new List<(AsnSeries, CongestionEvent)>();
                clusters.Add(current);
                currentEnd = c.Evt.End;
            }
            current.Add(c);
            if (c.Evt.End > currentEnd) currentEnd = c.Evt.End;
        }
        return clusters;
    }

    private static bool Overlaps(DateTime aStart, DateTime aEnd, DateTime bStart, DateTime bEnd) =>
        aStart < bEnd && bStart < aEnd;
}
