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

        var anchored = allSeries.Where(s => HasTrace(s, topology)).ToList();

        // A hop counts as elevated if it fired its own event OR shows in-window RTT excursions
        // above its baseline. The softer test matters for propagation: a downstream hop inherits
        // a real bottleneck's delay as spikes that are often too sparse to fire a sustained event,
        // and without it the localizer would wrongly absolve a genuine bottleneck as noise.
        bool IsElevated(AsnSeries s, DateTime start, DateTime end) =>
            (eventsBySeries.TryGetValue(s, out var evs) && evs.Any(e => Overlaps(e.Start, e.End, start, end)))
            || ElevatedInWindow(s, start, end, options);

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
            // A clean anchored series anywhere means the elevation is NOT line-wide. Kept for the
            // Confirmed confidence below (NOT the self-infliction gate - see lineWideUnderLoad).
            // Must have data in the window - a no-data series (newer target / gap) isn't "clean".
            var cleanControlExists = anchored.Any(s =>
                HasDataInWindow(s, window.Start, window.End) && !IsElevated(s, window.Start, window.End));
            // Self-inflicted access bufferbloat adds a near-constant rise to (almost) every monitored
            // path crossing the egress under load. The absolute elevation bar misses that on high-
            // baseline / high-variance paths (their inflated p90 hides a ~1 ms drift), so one such path
            // reading "clean" wrongly vetoes self-infliction. This robust median-shift test keys on
            // "did everything drift up together": the fraction of anchored paths (with data) whose
            // in-window median rose above baseline by a small margin. Only consulted for the egress hop.
            var anchoredWithData = anchored.Where(s => HasDataInWindow(s, window.Start, window.End)).ToList();
            var lineWideUnderLoad = anchoredWithData.Count > 0
                && anchoredWithData.Count(s => RoseInWindow(s, window.Start, window.End, options))
                    >= anchoredWithData.Count * options.CongestionLineWideRiseFraction;

            foreach (var (bnIp, members) in byBottleneck)
                result.Add(BuildLocalized(bnIp, members, allSeries, eventsBySeries, window,
                    topology, options, loadCoincident, cleanControlExists, lineWideUnderLoad, IsElevated));

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

        // Collapse genuinely shared incidents: where many independent hops are up TOGETHER (whether
        // each crossed its own gate or merely drifted up sub-threshold), replace the per-hop events
        // with one shared-upstream event spanning the concurrent window. Catches both the line-wide
        // cluster the per-hop localizer would scatter into N rows and the sub-gate rise it misses
        // entirely - keyed on bucket-level concurrency, so one hop lingering past the rest neither
        // hides the coincidence nor stretches the collapsed event's duration.
        ApplySharedUpstream(result, anchored, eventsBySeries, topology, options);

        return result.OrderBy(e => e.Start).ToList();
    }

    /// <summary>
    /// Collapses genuinely shared incidents into one event. A hop is "up" in a bucket if it fired its
    /// own congestion event there OR merely drifted up sub-threshold (its bucket median rose past
    /// baseline by a scale-relative margin a fixed millisecond gate would miss). Where a majority of
    /// independent hops are up TOGETHER, that is one shared upstream bottleneck, not N per-hop events:
    /// the per-hop events overlapping the concurrent window are removed and replaced with a single
    /// event attributed to the convergence hop closest to the user. Keyed on bucket-level concurrency
    /// (not the cluster's outer span), so one hop lingering past the rest neither hides the
    /// coincidence nor stretches the collapsed duration. Under load it reads as self-inflicted Loaded
    /// Latency (suppressed); otherwise it is real shared upstream congestion (Confirmed, scored).
    /// </summary>
    private static void ApplySharedUpstream(
        List<CongestionEvent> result,
        IReadOnlyList<AsnSeries> anchored,
        Dictionary<AsnSeries, List<CongestionEvent>> eventsBySeries,
        CongestionTopology topology,
        IspHealthOptions options)
    {
        var hops = anchored.Where(s => !s.IsDestination).ToList();
        if (hops.Count == 0) return;

        var bucketSize = TimeSpan.FromMinutes(options.CongestionBucketMinutes);
        var perHop = new List<(AsnSeries Hop, double Base, Dictionary<DateTime, double> Buckets, bool IsAccess)>();
        foreach (var h in hops)
        {
            var rtts = h.Samples.Where(s => s.RttAvgMs.HasValue).Select(s => s.RttAvgMs!.Value).ToList();
            var baseRtt = SeriesStats.Median(rtts);
            if (!baseRtt.HasValue) continue;
            var buckets = h.Samples.Where(s => s.RttAvgMs.HasValue)
                .GroupBy(s => CongestionDetector.FloorTime(s.Time, bucketSize))
                .ToDictionary(g => g.Key, g => SeriesStats.Median(g.Select(s => s.RttAvgMs!.Value).ToList()) ?? baseRtt.Value);
            var isAccess = h.HopIps.Any(ip => topology.AccessEgressHopIps.Contains(ip));
            perHop.Add((h, baseRtt.Value, buckets, isAccess));
        }
        if (perHop.Count == 0) return;

        // "Up" in a bucket: fired an event covering it, or drifted up sub-threshold (scale-relative).
        bool Up((AsnSeries Hop, double Base, Dictionary<DateTime, double> Buckets, bool IsAccess) p, DateTime t)
        {
            if (eventsBySeries.TryGetValue(p.Hop, out var evs) && evs.Any(e => Overlaps(e.Start, e.End, t, t + bucketSize)))
                return true;
            return p.Buckets.TryGetValue(t, out var med)
                && med >= p.Base + Math.Max(options.CongestionLineWideMinShiftMs, p.Base * options.CoincidentCongestionRttRisePct);
        }

        var upAt = new Dictionary<DateTime, List<AsnSeries>>();
        foreach (var t in perHop.SelectMany(p => p.Buckets.Keys).Distinct())
        {
            var up = perHop.Where(p => p.Buckets.ContainsKey(t) && Up(p, t)).ToList();
            // Shared upstream: the elevation is rooted at the access egress (the hop nearest you is up,
            // not merely a deeper hop with clean upstream) AND spans multiple independent networks.
            // That separates one shared incident from a single localized bottleneck that simply
            // propagated to its downstream victim - which must still localize, not collapse.
            var accessUp = up.Any(p => p.IsAccess);
            var asns = up.Select(p => p.Hop.AsnNumber).Where(a => a > 0).Distinct().Count();
            if (accessUp && asns >= options.SharedEventMinAsns)
                upAt[t] = up.Select(p => p.Hop).ToList();
        }
        if (upAt.Count == 0) return;

        // Group consecutive concurrent buckets (allowing a single-bucket gap) into windows.
        var groups = new List<List<DateTime>>();
        foreach (var t in upAt.Keys.OrderBy(t => t))
        {
            if (groups.Count > 0 && t - groups[^1][^1] <= bucketSize + bucketSize)
                groups[^1].Add(t);
            else
                groups.Add(new List<DateTime> { t });
        }

        int HopNum(AsnSeries s) => s.HopIps.Concat(s.AncestorIps)
            .Where(topology.HopNumberByIp.ContainsKey)
            .Select(ip => topology.HopNumberByIp[ip]).DefaultIfEmpty(int.MaxValue).Min();

        foreach (var g in groups)
        {
            var start = g[0];
            var end = g[^1] + bucketSize;
            var upHops = g.SelectMany(t => upAt[t]).Distinct().ToList();
            if (upHops.Select(h => h.AsnNumber).Where(a => a > 0).Distinct().Count() < options.SharedEventMinAsns) continue;

            // The nearest up hop owns the event: the shared bottleneck sits at or behind the
            // convergence point closest to the user, and attributing there avoids penalizing every
            // downstream transit ASN that merely inherited the upstream rise.
            var owner = upHops.OrderBy(HopNum).First();
            var ownerPer = perHop.First(p => p.Hop == owner);
            var peakRtt = g.Where(t => ownerPer.Buckets.ContainsKey(t)).Select(t => ownerPer.Buckets[t]).DefaultIfEmpty(ownerPer.Base).Max();
            var ownerJits = owner.Samples.Select(s => s.EffectiveJitterMs).Where(j => j.HasValue).Select(j => j!.Value).ToList();
            var baseJit = SeriesStats.Median(ownerJits) ?? 0;
            var peakJit = owner.Samples.Where(s => s.Time >= start && s.Time < end)
                .Select(s => s.EffectiveJitterMs).Where(j => j.HasValue).Select(j => j!.Value).DefaultIfEmpty(baseJit).Max();
            var loadCoincident = LoadCoincident(start, end, topology, options);

            // This concurrent window is one shared incident: drop the per-hop events it subsumes and
            // replace them with a single shared-upstream event spanning only the concurrent window.
            result.RemoveAll(e => !e.SharedUpstream && Overlaps(e.Start, e.End, start, end));
            result.Add(SharedUpstreamEvent(owner, ownerPer.Base, peakRtt, baseJit, peakJit, start, end, upHops.Count, loadCoincident));
        }
    }

    /// <summary>
    /// Builds one shared-upstream congestion event attributed to <paramref name="owner"/> (the
    /// convergence hop closest to the user). Under load it reads as self-inflicted "Loaded Latency"
    /// (your access link is the limit, suppressed from the score); otherwise it is real shared
    /// upstream congestion (Confirmed, scored). Used both to collapse a line-wide cluster of per-hop
    /// events and to surface a sub-gate coincident rise.
    /// </summary>
    private static CongestionEvent SharedUpstreamEvent(
        AsnSeries owner, double baselineRtt, double peakRtt, double baselineJit, double peakJit,
        DateTime start, DateTime end, int riseCount, bool loadCoincident) => new()
    {
        Start = start,
        End = end,
        AsnNumbers = owner.AsnNumber > 0 ? new List<int> { owner.AsnNumber } : new List<int>(),
        AsnNames = owner.AsnName is { Length: > 0 } ? new List<string> { owner.AsnName } : new List<string>(),
        TargetIds = owner.TargetIds.ToList(),
        BaselineRttMs = baselineRtt,
        PeakRttMs = peakRtt,
        BaselineJitterMs = baselineJit,
        PeakJitterMs = peakJit,
        Scope = CongestionScope.Unlocalized,
        Disposition = loadCoincident ? CongestionDisposition.SelfInflicted : CongestionDisposition.Confirmed,
        BottleneckHopIp = owner.HopIps.FirstOrDefault(),
        BottleneckLabel = owner.AsnName,
        SharedUpstream = true,
        LoadCoincident = loadCoincident,
        Confidence = 70,
        AttributionReason = loadCoincident
            ? "Every monitored path rose together under heavy WAN load, so the limit was your access link (bufferbloat or a congested shared-access network), not a single hop."
            : $"{riseCount} monitored paths across independent networks rose together this window - a shared upstream bottleneck lifting the whole path, not any one hop."
    };

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
        Func<AsnSeries, DateTime, DateTime, bool> isElevated)
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
        var cleanParallelPaths = allSeries.Count(s => HasTrace(s, topology)
            && HasDataInWindow(s, window.Start, window.End)
            && !s.HopIps.Contains(bottleneckIp, StringComparer.OrdinalIgnoreCase)
            && !s.AncestorIps.Contains(bottleneckIp, StringComparer.OrdinalIgnoreCase)
            && !isElevated(s, window.Start, window.End));

        CongestionDisposition disposition;
        string reason;
        if (isAccessEgress && loadCoincident && lineWideUnderLoad)
        {
            // Bottleneck is the access egress AND every monitored path drifted up together under load
            // (line-wide). That's loaded latency where all your traffic converges, not a distinct ISP
            // hop. Surfaced as "Loaded Latency" (not Congestion), not scored (Suppressed). We assert
            // location + correlation only; the mechanism (CPE buffer, OLT, policing) is unknowable here.
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
        else if (hasDescendant && lineWideUnderLoad)
        {
            // The elevation didn't forward to THIS hop's immediate downstream witness, which alone
            // would read as control-plane noise. But nearly every monitored path rose together this
            // window (line-wide, with or without local load) - that's a shared upstream bottleneck
            // lifting the whole path, not one hop's own ICMP handling. Real congestion, so scored.
            disposition = CongestionDisposition.Confirmed;
            reason = "Elevation rose across nearly every monitored path this window - a shared upstream bottleneck, not this hop's own control-plane handling.";
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
    /// A softer "elevated in this window" test than firing a full congestion event: true when a
    /// meaningful fraction of in-window RTT samples exceed the hop's own baseline p90 by the
    /// congestion RTT floor. A real bottleneck's added delay reaches downstream hops as excursions
    /// that may be too sparse to fire their own sustained event; using this for the propagation
    /// and clean-control checks stops the localizer from absolving a genuine bottleneck as
    /// control-plane noise just because nothing downstream fired. Clean off-path hops sit near zero.
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
        if (!baselineP90.HasValue) return false;
        var threshold = baselineP90.Value + options.CongestionRttMinDeltaMs;
        var excursionFraction = inWindow.Count(v => v > threshold) / (double)inWindow.Count;
        return excursionFraction >= options.CongestionPropagationExcursionFraction;
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
        var eMed = SeriesStats.Median(inWindow);
        return bMed.HasValue && eMed.HasValue && eMed.Value > bMed.Value + options.CongestionLineWideMinShiftMs;
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
