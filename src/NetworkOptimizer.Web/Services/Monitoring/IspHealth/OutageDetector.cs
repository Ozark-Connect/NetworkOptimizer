namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// Detects internet-unreachable outages: spans where the destination/internet targets go
/// to near-total packet loss while probes keep reporting. The reporting requirement is the
/// crux of the gap-vs-outage distinction - when the UniFi Console (gateway) drops, the
/// Monitoring Agent stops probing, so there are no samples at all and nothing is flagged; a
/// real upstream outage keeps the gateway up and the Monitoring Agent records 100% loss.
/// Detection is
/// group-based on the internet tier alone (shape-independent), because an outage is scored
/// by duration regardless of which hops dropped. The per-hop series shape the event only:
/// the deepest hop that stayed reachable is where the break sat, and each hop's recovery
/// time draws the inside-out heal (validated on a real AT&T outage where the OLT recovered
/// ~10 min before the upstream). Thresholds live in <see cref="IspHealthOptions"/>.
/// </summary>
public static class OutageDetector
{
    /// <summary>
    /// One monitored hop, carried for the outage shape and break attribution. <paramref name="Groupable"/>
    /// hops (the per-target access ISP rows) that end up sharing an outage signature are collapsed
    /// into one waterfall row; non-groupable hops (transit clusters, internet endpoints) always
    /// stay on their own. <paramref name="AsnLabel"/> is the ASN-level label a row prefers over its
    /// per-target <paramref name="Name"/> (the PTR hostname); the detector disambiguates when one
    /// ASN owns several rows. Null AsnLabel (internet endpoints) falls back to <paramref name="Name"/>.
    /// </summary>
    public sealed record Hop(string Name, int Depth, IReadOnlyList<LatencySample> Series, bool Groupable = false, string? AsnLabel = null, bool IsGateway = false);

    /// <param name="triggerTargets">The internet/destination loss series whose near-total loss defines an outage.</param>
    /// <param name="hops">Every monitored hop, ordered by distance (Depth ascending = nearest first), for the shape.</param>
    public static List<OutageEvent> Detect(
        IReadOnlyList<IReadOnlyList<LatencySample>> triggerTargets,
        IReadOnlyList<Hop> hops,
        IspHealthOptions options)
    {
        if (triggerTargets.Count == 0) return new List<OutageEvent>();

        var windowSize = TimeSpan.FromSeconds(options.OutageBucketSeconds);
        var triggerByBucket = BucketTargets(triggerTargets, windowSize);

        // Outage buckets: enough internet targets reporting (a bucket with none is a
        // monitoring gap, not an outage), and a strong majority of them dark.
        var outageBuckets = triggerByBucket
            .Where(kv => kv.Value.Count >= options.OutageMinReportingTargets
                && DarkFraction(kv.Value, options) >= options.OutageCoverageFraction)
            .Select(kv => kv.Key)
            .OrderBy(t => t)
            .ToList();

        var hopBuckets = hops.ToDictionary(h => h, h => BucketTargets(new[] { h.Series }, windowSize));

        // Contiguous runs of outage buckets (gap of one window ends a run), then coalesce
        // runs separated by a short healthy gap (OutageMaxGapSeconds). One real outage dips
        // below the dark-fraction gate for a bucket or two during staggered onset/recovery
        // (targets go dark and heal at slightly different times) - without the coalesce that
        // shatters it into several events. Sealing the gap before duration-filtering also lets
        // two sub-min-duration runs straddling a blip add up to one qualifying outage.
        var runs = new List<(DateTime Start, DateTime End)>();
        for (var i = 0; i < outageBuckets.Count;)
        {
            var j = i;
            while (j + 1 < outageBuckets.Count && outageBuckets[j + 1] - outageBuckets[j] <= windowSize)
                j++;
            runs.Add((outageBuckets[i], outageBuckets[j] + windowSize)); // through the last dark bucket
            i = j + 1;
        }

        var maxGap = TimeSpan.FromSeconds(options.OutageMaxGapSeconds);
        var merged = new List<(DateTime Start, DateTime End)>();
        foreach (var run in runs)
        {
            if (merged.Count > 0 && run.Start - merged[^1].End <= maxGap)
                merged[^1] = (merged[^1].Start, run.End);
            else
                merged.Add(run);
        }

        var events = new List<OutageEvent>();
        var minDuration = TimeSpan.FromSeconds(options.OutageMinDurationSeconds);
        foreach (var (start, end) in merged)
        {
            if (end - start < minDuration) continue;
            events.Add(BuildEvent(start, end, triggerTargets, hops, hopBuckets, options));
        }
        return events;
    }

    /// <summary>
    /// Detects brief partial-loss disruptions: short windows where many independent path targets
    /// degrade together (loss >= <see cref="IspHealthOptions.OutagePartialLossPct"/>) without any
    /// reaching the near-total dark threshold. Distinct from <see cref="Detect"/> (blackouts) - it
    /// keys on coincident breadth across targets and ASNs, the signal that partial loss is a real
    /// path event rather than one lossy probe target. The breadth gate uses a wider bucket than the
    /// blackout pass because partial loss is route-specific: independent targets degrade at slightly
    /// different instants, so they only land together over a longer window. Windows overlapping a
    /// blackout in <paramref name="darkWindows"/> are skipped so the two passes don't double-count.
    /// </summary>
    /// <param name="pathHops">Every monitored non-gateway path hop (access/transit/internet), for breadth and shape.</param>
    /// <param name="darkWindows">Blackout outage spans already found by <see cref="Detect"/>, to exclude.</param>
    public static List<OutageEvent> DetectPartial(
        IReadOnlyList<Hop> pathHops,
        IReadOnlyList<(DateTime Start, DateTime End)> darkWindows,
        IspHealthOptions options)
    {
        var pool = pathHops.Where(h => !h.IsGateway).ToList();
        if (pool.Count == 0) return new List<OutageEvent>();

        var windowSize = TimeSpan.FromSeconds(options.OutagePartialBucketSeconds);
        var hopBuckets = pool.ToDictionary(h => h, h => BucketTargets(new[] { h.Series }, windowSize));
        var partialPct = options.OutagePartialLossPct;

        // A bucket qualifies when enough distinct targets across enough distinct ASNs degrade
        // together: coincident loss across independent destinations is a path event, a lone lossy
        // target is noise.
        bool BucketQualifies(DateTime t)
        {
            var degraded = pool.Where(h => hopBuckets[h].TryGetValue(t, out var l) && l.Count > 0 && l[0] >= partialPct).ToList();
            var asns = degraded.Select(h => h.AsnLabel ?? h.Name).Distinct().Count();
            return degraded.Count >= options.OutagePartialMinTargets && asns >= options.OutagePartialMinAsns;
        }

        var qualifying = hopBuckets.Values.SelectMany(d => d.Keys).Distinct()
            .Where(BucketQualifies)
            .OrderBy(t => t).ToList();
        if (qualifying.Count == 0) return new List<OutageEvent>();

        // Contiguous runs (gap of one window ends a run), then coalesce across a short healthy gap.
        var runs = new List<(DateTime Start, DateTime End)>();
        for (var i = 0; i < qualifying.Count;)
        {
            var j = i;
            while (j + 1 < qualifying.Count && qualifying[j + 1] - qualifying[j] <= windowSize) j++;
            runs.Add((qualifying[i], qualifying[j] + windowSize));
            i = j + 1;
        }
        var maxGap = TimeSpan.FromSeconds(options.OutageMaxGapSeconds);
        var merged = new List<(DateTime Start, DateTime End)>();
        foreach (var run in runs)
        {
            if (merged.Count > 0 && run.Start - merged[^1].End <= maxGap)
                merged[^1] = (merged[^1].Start, run.End);
            else merged.Add(run);
        }

        var events = new List<OutageEvent>();
        var minDuration = TimeSpan.FromSeconds(options.OutagePartialMinDurationSeconds);
        foreach (var (start, end) in merged)
        {
            if (end - start < minDuration) continue;
            // A blackout also clears the partial threshold, so a window already flagged as a
            // blackout would otherwise surface twice - skip any that overlaps one.
            if (darkWindows.Any(w => start < w.End && w.Start < end)) continue;
            events.Add(BuildPartialEvent(start, end, pool, hopBuckets, options));
        }
        return events;
    }

    private static OutageEvent BuildPartialEvent(
        DateTime start, DateTime end,
        List<Hop> pool,
        Dictionary<Hop, Dictionary<DateTime, List<double>>> hopBuckets,
        IspHealthOptions options)
    {
        var partialPct = options.OutagePartialLossPct;
        // Degraded duty cycle per hop, mirroring the blackout attribution but at the partial
        // threshold: the break sits just beyond the deepest hop that stayed clean.
        var degradedDepth = new Dictionary<int, bool>();
        var degraded = new List<(Hop Hop, double Peak)>();
        double eventPeak = 0;
        foreach (var hop in pool.OrderBy(h => h.Depth))
        {
            double peak = 0;
            int degradedBuckets = 0, totalBuckets = 0;
            foreach (var (_, losses) in hopBuckets[hop].Where(kv => kv.Key >= start && kv.Key < end))
            {
                if (losses.Count == 0) continue;
                totalBuckets++;
                peak = Math.Max(peak, losses[0]);
                if (losses[0] >= partialPct) degradedBuckets++;
            }
            if (totalBuckets == 0) continue;
            degradedDepth[hop.Depth] = (double)degradedBuckets / totalBuckets >= 0.5;
            if (degradedBuckets > 0)
            {
                eventPeak = Math.Max(eventPeak, peak);
                degraded.Add((hop, peak));
            }
        }

        // Unlike the blackout waterfall (which groups access hops via GroupAccessTiers), partial-loss
        // tiers are per-hop, so several hops of one ASN would all read as just the ASN label. Only when
        // a label repeats in this list, disambiguate it with the specific hop's name/hostname tail.
        var labelCounts = degraded.GroupBy(d => d.Hop.AsnLabel ?? d.Hop.Name).ToDictionary(g => g.Key, g => g.Count());
        var tiers = degraded.Select(d =>
        {
            var label = d.Hop.AsnLabel ?? d.Hop.Name;
            return new OutageTierState
            {
                Name = DisambiguateTierLabel(label, d.Hop.Name, labelCounts[label] > 1),
                Depth = d.Hop.Depth,
                PeakLossPct = d.Peak,
                WentDark = false,
                RecoveredAt = null
            };
        }).ToList();

        var nearest = pool.Where(h => degradedDepth.ContainsKey(h.Depth)).OrderBy(h => h.Depth).FirstOrDefault();
        var lastClean = pool.Where(h => degradedDepth.TryGetValue(h.Depth, out var d) && !d).OrderByDescending(h => h.Depth).FirstOrDefault();
        var scope = nearest == null || degradedDepth[nearest.Depth] || lastClean == null
            ? OutageScope.FullWan
            : OutageScope.Upstream;

        var poolSeries = pool.Select(h => (IReadOnlyList<LatencySample>)h.Series).ToList();
        var onset = FirstDark(poolSeries, start, end, partialPct) ?? start;
        var recovery = PreciseRecovery(poolSeries, start, end, partialPct) ?? end;
        var reporting = pool.Count(h => hopBuckets[h].Any(kv => kv.Key >= start && kv.Key < end && kv.Value.Count > 0));

        return new OutageEvent
        {
            Start = onset,
            End = recovery,
            IsPartial = true,
            IsBrief = recovery - onset < TimeSpan.FromSeconds(options.OutageBriefMaxSeconds),
            PeakLossPct = eventPeak,
            DegradedTargetCount = tiers.Count,
            PathTargetCount = reporting,
            Scope = scope,
            LastReachableHop = scope == OutageScope.Upstream ? (lastClean!.AsnLabel ?? lastClean.Name) : null,
            Tiers = tiers.OrderBy(t => t.Depth).ToList()
        };
    }

    /// <summary>Per bucket, the list of each reporting target's mean loss in that bucket.</summary>
    private static Dictionary<DateTime, List<double>> BucketTargets(
        IReadOnlyList<IReadOnlyList<LatencySample>> targets, TimeSpan windowSize)
    {
        var perBucket = new Dictionary<DateTime, List<double>>();
        foreach (var target in targets)
        {
            foreach (var g in target.Where(s => s.LossPercent.HasValue)
                         .GroupBy(s => CongestionDetector.FloorTime(s.Time, windowSize)))
            {
                if (!perBucket.TryGetValue(g.Key, out var list))
                {
                    list = new List<double>();
                    perBucket[g.Key] = list;
                }
                list.Add(g.Average(s => s.LossPercent!.Value));
            }
        }
        return perBucket;
    }

    private static double DarkFraction(List<double> targetLosses, IspHealthOptions options) =>
        targetLosses.Count == 0 ? 0 : (double)targetLosses.Count(l => l >= options.OutageDarkLossPct) / targetLosses.Count;

    private static OutageEvent BuildEvent(
        DateTime start, DateTime end,
        IReadOnlyList<IReadOnlyList<LatencySample>> triggerTargets,
        IReadOnlyList<Hop> hops,
        Dictionary<Hop, Dictionary<DateTime, List<double>>> hopBuckets,
        IspHealthOptions options)
    {
        var tiers = new List<(Hop Hop, OutageTierState State)>();
        // A hop on the broken path stays dark for (most of) the outage; a hop that merely
        // blipped at onset then held - like the OLT, which recovered ~10 min before the
        // upstream in the validation data - is NOT the break and must read as reachable.
        // So attribution uses the dark duty cycle, not "ever went dark".
        var onBrokenPath = new Dictionary<int, bool>();
        foreach (var hop in hops.OrderBy(h => h.Depth))
        {
            double peakLoss = 0;
            int darkBuckets = 0, totalBuckets = 0;
            DateTime? lastDarkBucket = null;
            foreach (var (bucketStart, losses) in hopBuckets[hop]
                         .Where(kv => kv.Key >= start && kv.Key < end)
                         .OrderBy(kv => kv.Key))
            {
                if (losses.Count == 0) continue;
                totalBuckets++;
                var mean = losses.Average();
                peakLoss = Math.Max(peakLoss, mean);
                if (mean >= options.OutageDarkLossPct)
                {
                    darkBuckets++;
                    lastDarkBucket = bucketStart;
                }
            }
            // No samples in the outage window means the target didn't exist or wasn't enabled
            // then - it has nothing to say about this outage, so don't give it a row.
            if (totalBuckets == 0) continue;
            onBrokenPath[hop.Depth] = (double)darkBuckets / totalBuckets >= 0.5;
            tiers.Add((hop, new OutageTierState
            {
                Name = hop.Name,
                Depth = hop.Depth,
                PeakLossPct = peakLoss,
                WentDark = darkBuckets > 0,
                // Recovery is anchored to the last *sustained* dark bucket, not the last dark
                // sample: a hop can twitch dark for a single probe late in the outage (the OLT
                // blipping as the upstream heals) without that being a real relapse. We then read
                // the first good sample at/after that bucket, so the time still carries seconds.
                RecoveredAt = lastDarkBucket.HasValue
                    ? RecoveryAfter(hop.Series, lastDarkBucket.Value, options.OutageDarkLossPct)
                    : null
            }));
        }

        // The break sits just beyond the deepest hop that stayed reachable through the
        // outage. If even the nearest hop was dark for most of it, the whole WAN dropped.
        // Attribution runs on the per-hop (ungrouped) states so it can name the precise hop.
        // The LAN gateway is excluded from WAN break attribution (FullWan/Upstream) and only decides
        // the Local override: when the gateway itself stayed dark through the outage, the agent could
        // not reach its own gateway - a LAN/switch/gateway outage, not the ISP's WAN. With no gateway
        // hop, wanStates == all states and the FullWan/Upstream logic is byte-for-byte unchanged.
        var gwTier = tiers.FirstOrDefault(t => t.Hop.IsGateway);
        var gatewayDark = gwTier.Hop != null && onBrokenPath.TryGetValue(gwTier.State.Depth, out var gwd) && gwd;
        var wanStates = tiers.Where(t => !t.Hop.IsGateway).Select(t => t.State).ToList();
        var nearest = wanStates.OrderBy(s => s.Depth).FirstOrDefault();
        var lastReachable = wanStates.Where(s => !onBrokenPath[s.Depth]).OrderByDescending(s => s.Depth).FirstOrDefault();
        var scope = gatewayDark
            ? OutageScope.Local
            : nearest == null || onBrokenPath[nearest.Depth] || lastReachable == null
                ? OutageScope.FullWan
                : OutageScope.Upstream;

        // Bucket edges are minute-aligned; report the real onset and recovery instants from
        // the pooled trigger stream so the window carries seconds. Fall back to the bucket
        // edges if the precise edges can't be found (e.g. outage still ongoing at window end).
        var onset = FirstDark(triggerTargets, start, end, options.OutageDarkLossPct) ?? start;
        var recovery = PreciseRecovery(triggerTargets, start, end, options.OutageDarkLossPct) ?? end;

        // Breadth/depth over the reporting trigger (internet) targets, so a blackout carries the same
        // severity = breadth x depth the scorer reads for partials: how many reporting targets went
        // dark, and the worst loss reached. By the coverage gate breadth is already >= the dark
        // fraction, but a near-total widespread drop still reads hotter than a borderline one.
        int reportingTargets = 0, darkTargets = 0;
        double eventPeakLoss = 0;
        foreach (var target in triggerTargets)
        {
            var inWindow = target.Where(s => s.LossPercent.HasValue && s.Time >= start && s.Time < end).ToList();
            if (inWindow.Count == 0) continue;
            reportingTargets++;
            eventPeakLoss = Math.Max(eventPeakLoss, inWindow.Max(s => s.LossPercent!.Value));
            if (inWindow.Any(s => s.LossPercent!.Value >= options.OutageDarkLossPct)) darkTargets++;
        }

        return new OutageEvent
        {
            Start = onset,
            End = recovery,
            IsBrief = recovery - onset < TimeSpan.FromSeconds(options.OutageBriefMaxSeconds),
            PeakLossPct = eventPeakLoss,
            DegradedTargetCount = darkTargets,
            PathTargetCount = reportingTargets,
            Scope = scope,
            LastReachableHop = scope == OutageScope.Upstream ? lastReachable!.Name : null,
            Tiers = GroupAccessTiers(tiers, options)
        };
    }

    /// <summary>
    /// Collapses groupable (access ISP) tiers that share an outage signature - recovery within
    /// <see cref="IspHealthOptions.OutageAccessGroupToleranceSeconds"/> - into one row, so a handful
    /// of near-identical access hops don't each take a row, and labels every row by its ASN.
    /// Access hops that recovered at a distinctly different time (the inside-out heal, e.g. the OLT
    /// coming back first) stay separate. When one ASN owns several rows they're disambiguated:
    /// a merged group reads "ASN (N hops)", a lone hop "ASN (hostname tail)"; a unique ASN just
    /// shows its name. Non-groupable tiers (transit clusters, internet endpoints) always keep their
    /// own row. Returned nearest-first by depth.
    /// </summary>
    private static List<OutageTierState> GroupAccessTiers(
        List<(Hop Hop, OutageTierState State)> tiers, IspHealthOptions options)
    {
        var tol = TimeSpan.FromSeconds(options.OutageAccessGroupToleranceSeconds);
        var result = new List<OutageTierState>();

        // Transit clusters and internet endpoints keep their own row, labeled by the ASN (transit)
        // or the endpoint name (internet, AsnLabel null).
        foreach (var (hop, state) in tiers.Where(t => !t.Hop.Groupable))
            result.Add(Relabel(state, hop.AsnLabel ?? hop.Name));

        // Access hops: WITHIN each ASN, cluster by outage signature - never merge across ASNs (a
        // dual-WAN could have two access ISPs recover at the same instant). "Up" and "dark, never
        // recovered" are each one shared signature; "dark and recovered" clusters by recovery time.
        var groups = new List<List<(Hop Hop, OutageTierState State)>>();
        void AddGroup(IEnumerable<(Hop, OutageTierState)> g) { var l = g.ToList(); if (l.Count > 0) groups.Add(l); }
        foreach (var asnGroup in tiers.Where(t => t.Hop.Groupable).GroupBy(t => t.Hop.AsnLabel ?? t.Hop.Name))
        {
            var members = asnGroup.ToList();
            AddGroup(members.Where(t => !t.State.WentDark));
            AddGroup(members.Where(t => t.State.WentDark && !t.State.RecoveredAt.HasValue));
            var cluster = new List<(Hop, OutageTierState)>();
            foreach (var t in members.Where(t => t.State.WentDark && t.State.RecoveredAt.HasValue).OrderBy(t => t.State.RecoveredAt!.Value))
            {
                if (cluster.Count > 0 && t.State.RecoveredAt!.Value - cluster[0].Item2.RecoveredAt!.Value > tol)
                {
                    groups.Add(cluster);
                    cluster = new List<(Hop, OutageTierState)>();
                }
                cluster.Add(t);
            }
            if (cluster.Count > 0) groups.Add(cluster);
        }

        // A unique ASN shows just its name; several rows split into "(N hops)" / "(hostname tail)".
        // Key and lookup use the SAME nearest-hop expression so they never diverge.
        static string AsnOf(List<(Hop Hop, OutageTierState State)> g)
        {
            var n = g.OrderBy(m => m.State.Depth).First().Hop;
            return n.AsnLabel ?? n.Name;
        }
        var rowsPerAsn = groups.GroupBy(AsnOf).ToDictionary(x => x.Key, x => x.Count());
        foreach (var g in groups)
        {
            var nearest = g.OrderBy(m => m.State.Depth).First();
            var asn = AsnOf(g);
            var name = rowsPerAsn[asn] == 1 ? asn
                : g.Count > 1 ? $"{asn} ({g.Count} hops)"
                : $"{asn} ({HostnameTail(nearest.Hop.Name, asn)})";
            result.Add(new OutageTierState
            {
                Name = name,
                Depth = nearest.State.Depth,
                PeakLossPct = g.Max(m => m.State.PeakLossPct),
                WentDark = g.Any(m => m.State.WentDark),
                RecoveredAt = g.Where(m => m.State.RecoveredAt.HasValue).Select(m => m.State.RecoveredAt).DefaultIfEmpty(null).Min()
            });
        }

        return result.OrderBy(s => s.Depth).ToList();
    }

    private static OutageTierState Relabel(OutageTierState s, string name) => new()
    {
        Name = name,
        Depth = s.Depth,
        PeakLossPct = s.PeakLossPct,
        WentDark = s.WentDark,
        RecoveredAt = s.RecoveredAt
    };

    /// <summary>The part of a per-target name after its ASN prefix ("AT&amp;T nokia-olt" -> "nokia-olt").</summary>
    private static string HostnameTail(string name, string asn) =>
        name.StartsWith(asn + " ", StringComparison.OrdinalIgnoreCase) ? name[(asn.Length + 1)..] : name;

    /// <summary>
    /// When a tier label repeats within one event's list (e.g. several hops of one access ISP), make
    /// it specific: keep the hop's own name if it already extends the label (e.g. a cluster's
    /// "+N ms hop"), otherwise append the hostname tail as "ASN (tail)" - matching the blackout
    /// waterfall's style. A unique label is returned unchanged.
    /// </summary>
    private static string DisambiguateTierLabel(string label, string hopName, bool repeats)
    {
        if (!repeats || string.IsNullOrEmpty(hopName) || string.Equals(hopName, label, StringComparison.OrdinalIgnoreCase))
            return label;
        if (hopName.StartsWith(label + " ", StringComparison.OrdinalIgnoreCase))
            return hopName; // already "label hostname" or "label (+N ms hop)"
        return $"{label} ({HostnameTail(hopName, label)})";
    }

    /// <summary>Actual timestamp of the first sample at/above the loss threshold in [start, end).</summary>
    private static DateTime? FirstDark(
        IReadOnlyList<IReadOnlyList<LatencySample>> targets,
        DateTime start, DateTime end, double threshold) =>
        targets.SelectMany(t => t)
            .Where(s => s.LossPercent.HasValue && s.Time >= start && s.Time < end
                && s.LossPercent.Value >= threshold)
            .OrderBy(s => s.Time)
            .Select(s => (DateTime?)s.Time)
            .FirstOrDefault();

    /// <summary>
    /// First reporting sample below the dark threshold at or after a floor time - the instant
    /// the series came back (with seconds). Used with the last sustained dark bucket as the
    /// floor so a late single-probe twitch doesn't push recovery to the end of the outage.
    /// Null when no good sample follows (never recovered in-window).
    /// </summary>
    private static DateTime? RecoveryAfter(
        IReadOnlyList<LatencySample> series, DateTime floor, double darkPct) =>
        series
            .Where(s => s.LossPercent.HasValue && s.Time >= floor && s.LossPercent.Value < darkPct)
            .OrderBy(s => s.Time)
            .Select(s => (DateTime?)s.Time)
            .FirstOrDefault();

    /// <summary>
    /// When the targets came back: the first reporting sample below the dark threshold that
    /// follows the last dark sample in [start, end). That is the instant recovery was actually
    /// observed, with seconds. Null when nothing went dark or it never recovered in-window.
    /// </summary>
    private static DateTime? PreciseRecovery(
        IReadOnlyList<IReadOnlyList<LatencySample>> targets,
        DateTime start, DateTime end, double dark)
    {
        DateTime? lastDark = targets.SelectMany(t => t)
            .Where(s => s.LossPercent.HasValue && s.Time >= start && s.Time < end
                && s.LossPercent.Value >= dark)
            .OrderBy(s => s.Time)
            .Select(s => (DateTime?)s.Time)
            .LastOrDefault();
        if (!lastDark.HasValue) return null;

        return targets.SelectMany(t => t)
            .Where(s => s.LossPercent.HasValue && s.LossPercent.Value < dark && s.Time > lastDark.Value)
            .OrderBy(s => s.Time)
            .Select(s => (DateTime?)s.Time)
            .FirstOrDefault();
    }
}
