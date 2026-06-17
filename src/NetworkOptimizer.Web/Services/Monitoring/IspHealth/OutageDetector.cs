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
    /// <summary>One monitored hop, carried for the outage shape and break attribution.</summary>
    public sealed record Hop(string Name, int Depth, IReadOnlyList<LatencySample> Series);

    /// <param name="triggerTargets">The internet/destination loss series whose near-total loss defines an outage.</param>
    /// <param name="hops">Every monitored hop, ordered by distance (Depth ascending = nearest first), for the shape.</param>
    public static List<OutageEvent> Detect(
        IReadOnlyList<IReadOnlyList<LatencySample>> triggerTargets,
        IReadOnlyList<Hop> hops,
        IspHealthOptions options)
    {
        if (triggerTargets.Count == 0) return new List<OutageEvent>();

        var windowSize = TimeSpan.FromMinutes(options.OutageBucketMinutes);
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

        var events = new List<OutageEvent>();
        var minDuration = TimeSpan.FromMinutes(options.OutageMinDurationMinutes);
        for (var i = 0; i < outageBuckets.Count;)
        {
            // Extend a run over adjacent (contiguous) outage buckets; a non-outage bucket ends it.
            var j = i;
            while (j + 1 < outageBuckets.Count && outageBuckets[j + 1] - outageBuckets[j] <= windowSize)
                j++;

            var start = outageBuckets[i];
            var end = outageBuckets[j] + windowSize; // through the last dark bucket
            i = j + 1;

            if (end - start < minDuration) continue;
            events.Add(BuildEvent(start, end, triggerTargets, hops, hopBuckets, options));
        }
        return events;
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
        var states = new List<OutageTierState>();
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
            onBrokenPath[hop.Depth] = totalBuckets > 0 && (double)darkBuckets / totalBuckets >= 0.5;
            states.Add(new OutageTierState
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
            });
        }

        // The break sits just beyond the deepest hop that stayed reachable through the
        // outage. If even the nearest hop was dark for most of it, the whole WAN dropped.
        var nearest = states.OrderBy(s => s.Depth).FirstOrDefault();
        var lastReachable = states.Where(s => !onBrokenPath[s.Depth]).OrderByDescending(s => s.Depth).FirstOrDefault();
        var scope = nearest == null || onBrokenPath[nearest.Depth] || lastReachable == null
            ? OutageScope.FullWan
            : OutageScope.Upstream;

        // Bucket edges are minute-aligned; report the real onset and recovery instants from
        // the pooled trigger stream so the window carries seconds. Fall back to the bucket
        // edges if the precise edges can't be found (e.g. outage still ongoing at window end).
        var onset = FirstDark(triggerTargets, start, end, options) ?? start;
        var recovery = PreciseRecovery(triggerTargets, start, end, options) ?? end;

        return new OutageEvent
        {
            Start = onset,
            End = recovery,
            Scope = scope,
            LastReachableHop = scope == OutageScope.Upstream ? lastReachable!.Name : null,
            Tiers = states
        };
    }

    /// <summary>Actual timestamp of the first dark sample (loss at/above the dark threshold) in [start, end).</summary>
    private static DateTime? FirstDark(
        IReadOnlyList<IReadOnlyList<LatencySample>> targets,
        DateTime start, DateTime end, IspHealthOptions options) =>
        targets.SelectMany(t => t)
            .Where(s => s.LossPercent.HasValue && s.Time >= start && s.Time < end
                && s.LossPercent.Value >= options.OutageDarkLossPct)
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
        DateTime start, DateTime end, IspHealthOptions options)
    {
        var dark = options.OutageDarkLossPct;
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
