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
            events.Add(BuildEvent(start, end, hops, hopBuckets, windowSize, options));
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
        IReadOnlyList<Hop> hops,
        Dictionary<Hop, Dictionary<DateTime, List<double>>> hopBuckets,
        TimeSpan windowSize, IspHealthOptions options)
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
            DateTime? lastDark = null;
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
                    lastDark = bucketStart;
                }
            }
            onBrokenPath[hop.Depth] = totalBuckets > 0 && (double)darkBuckets / totalBuckets >= 0.5;
            states.Add(new OutageTierState
            {
                Name = hop.Name,
                Depth = hop.Depth,
                PeakLossPct = peakLoss,
                WentDark = darkBuckets > 0,
                RecoveredAt = darkBuckets > 0 && lastDark.HasValue ? lastDark.Value + windowSize : null
            });
        }

        // The break sits just beyond the deepest hop that stayed reachable through the
        // outage. If even the nearest hop was dark for most of it, the whole WAN dropped.
        var nearest = states.OrderBy(s => s.Depth).FirstOrDefault();
        var lastReachable = states.Where(s => !onBrokenPath[s.Depth]).OrderByDescending(s => s.Depth).FirstOrDefault();
        var scope = nearest == null || onBrokenPath[nearest.Depth] || lastReachable == null
            ? OutageScope.FullWan
            : OutageScope.Upstream;

        return new OutageEvent
        {
            Start = start,
            End = end,
            Scope = scope,
            LastReachableHop = scope == OutageScope.Upstream ? lastReachable!.Name : null,
            Tiers = states
        };
    }
}
