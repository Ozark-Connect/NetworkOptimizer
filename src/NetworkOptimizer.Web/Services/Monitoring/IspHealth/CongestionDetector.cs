namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// Detects sustained congestion events in per-ASN latency series: periods where
/// both RTT and jitter are elevated above robust baselines for a minimum duration.
/// Simultaneous events across multiple ASNs are merged into a single shared event,
/// because multi-path congestion usually indicates a shared upstream or return-path
/// bottleneck rather than independent problems in every transit network.
/// Thresholds live in <see cref="IspHealthOptions"/>; defaults are provisional until
/// validated against real incident data.
/// </summary>
public static class CongestionDetector
{
    public static List<CongestionEvent> Detect(IReadOnlyList<AsnSeries> allSeries, IspHealthOptions options)
    {
        var events = new List<CongestionEvent>();
        foreach (var series in allSeries)
        {
            events.AddRange(DetectForSeries(series, options));
        }
        return MergeSharedEvents(events, options);
    }

    /// <summary>Detects events within a single ASN's series. Exposed for replay against exported data.</summary>
    public static List<CongestionEvent> DetectForSeries(AsnSeries series, IspHealthOptions options)
    {
        var samples = series.Samples.Where(s => s.RttAvgMs.HasValue).OrderBy(s => s.Time).ToList();
        if (samples.Count == 0) return new List<CongestionEvent>();

        var allRtts = samples.Select(s => s.RttAvgMs!.Value).ToList();
        var allJitters = samples.Select(s => s.EffectiveJitterMs).Where(j => j.HasValue).Select(j => j!.Value).ToList();
        var baselineRtt = SeriesStats.Median(allRtts);
        var rttMad = SeriesStats.Mad(allRtts);
        var baselineP90 = SeriesStats.Percentile(allRtts, 0.90);
        var baselineJitter = allJitters.Count > 0 ? SeriesStats.Median(allJitters) : null;
        if (baselineRtt == null || rttMad == null || baselineP90 == null) return new List<CongestionEvent>();

        var bucketSize = TimeSpan.FromMinutes(options.CongestionBucketMinutes);
        var buckets = samples
            .GroupBy(s => FloorTime(s.Time, bucketSize))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var rtts = g.Where(s => s.RttAvgMs.HasValue).Select(s => s.RttAvgMs!.Value).ToList();
                return new Bucket(
                    g.Key,
                    SeriesStats.Median(rtts),
                    SeriesStats.Percentile(rtts, 0.90),
                    SeriesStats.Median(g.Select(s => s.EffectiveJitterMs).Where(j => j.HasValue).Select(j => j!.Value).ToList()));
            })
            .ToList();

        var rttThreshold = baselineRtt.Value + Math.Max(options.CongestionRttMinDeltaMs, options.CongestionRttDeltaFactor * rttMad.Value);
        var jitterThreshold = baselineJitter.HasValue ? options.CongestionJitterFactor * baselineJitter.Value : (double?)null;
        // Burst threshold: intermittent spikes lift the bucket p90 long before the
        // median moves; the spikes themselves are the jitter, so no separate jitter
        // gate applies on this path
        var burstSpread = Math.Max(baselineP90.Value - baselineRtt.Value, 0.5);
        var burstThreshold = baselineP90.Value + Math.Max(options.CongestionRttMinDeltaMs, options.CongestionBurstDeltaFactor * burstSpread);

        var events = new List<CongestionEvent>();
        var run = new List<Bucket>();
        var gap = 0;
        foreach (var bucket in buckets)
        {
            var sustainedElevated = bucket.RttMs.HasValue
                && bucket.RttMs.Value > rttThreshold
                && jitterThreshold.HasValue
                && bucket.JitterMs.HasValue
                && bucket.JitterMs.Value > jitterThreshold.Value
                // Absolute floor on the jitter rise, so an ultra-stable far hop (baseline
                // jitter near zero) does not trip the multiplicative gate on a fraction of a
                // millisecond of shared return-path wobble.
                && bucket.JitterMs.Value - baselineJitter!.Value >= options.CongestionJitterMinDeltaMs;
            // Burst shape only: p90 elevated while the median stays near baseline.
            // A flat fully-elevated bucket with no jitter is a route detour, not
            // congestion; that shape belongs to the step detector
            var burstElevated = bucket.P90Ms.HasValue
                && bucket.P90Ms.Value > burstThreshold
                && bucket.RttMs.HasValue
                && bucket.RttMs.Value >= baselineRtt.Value
                && bucket.RttMs.Value <= rttThreshold;
            var elevated = sustainedElevated || burstElevated;

            if (elevated)
            {
                run.Add(bucket);
                gap = 0;
            }
            else if (run.Count > 0 && gap == 0)
            {
                gap = 1;
            }
            else if (run.Count > 0)
            {
                FlushRun(run, series, baselineRtt.Value, baselineJitter ?? 0, bucketSize, options, events);
                run.Clear();
                gap = 0;
            }
        }
        FlushRun(run, series, baselineRtt.Value, baselineJitter ?? 0, bucketSize, options, events);
        return events;
    }

    private static void FlushRun(List<Bucket> run, AsnSeries series, double baselineRtt, double baselineJitter,
        TimeSpan bucketSize, IspHealthOptions options, List<CongestionEvent> events)
    {
        if (run.Count == 0) return;
        var start = run[0].Start;
        var end = run[^1].Start + bucketSize;
        if ((end - start).TotalMinutes < options.CongestionMinDurationMinutes) return;

        events.Add(new CongestionEvent
        {
            Start = start,
            End = end,
            AsnNumbers = { series.AsnNumber },
            AsnNames = { series.AsnName ?? $"AS{series.AsnNumber}" },
            TargetIds = series.TargetIds.ToList(),
            BaselineRttMs = baselineRtt,
            PeakRttMs = run.Max(b => b.RttMs ?? 0),
            BaselineJitterMs = baselineJitter,
            PeakJitterMs = run.Max(b => b.JitterMs ?? 0)
        });
    }

    /// <summary>
    /// Merges time-overlapping events across ASNs into shared upstream events when at
    /// least <see cref="IspHealthOptions.SharedEventMinAsns"/> ASNs are affected.
    /// </summary>
    public static List<CongestionEvent> MergeSharedEvents(List<CongestionEvent> events, IspHealthOptions options)
    {
        if (events.Count <= 1) return events;

        var clusters = new List<List<CongestionEvent>>();
        foreach (var evt in events.OrderBy(e => e.Start))
        {
            var cluster = clusters.FirstOrDefault(c => c.Any(other => Overlaps(evt, other, options.SharedEventOverlapFraction)));
            if (cluster != null) cluster.Add(evt);
            else clusters.Add(new List<CongestionEvent> { evt });
        }

        var merged = new List<CongestionEvent>();
        foreach (var cluster in clusters)
        {
            var distinctAsns = cluster.SelectMany(e => e.AsnNumbers).Distinct().ToList();
            if (cluster.Count == 1 || distinctAsns.Count < options.SharedEventMinAsns)
            {
                merged.AddRange(cluster);
                continue;
            }

            var worst = cluster.OrderByDescending(e => e.PeakRttMs - e.BaselineRttMs).First();
            merged.Add(new CongestionEvent
            {
                Start = cluster.Min(e => e.Start),
                End = cluster.Max(e => e.End),
                AsnNumbers = distinctAsns,
                AsnNames = cluster.SelectMany(e => e.AsnNames).Distinct().ToList(),
                TargetIds = cluster.SelectMany(e => e.TargetIds).Distinct().ToList(),
                BaselineRttMs = worst.BaselineRttMs,
                PeakRttMs = worst.PeakRttMs,
                BaselineJitterMs = worst.BaselineJitterMs,
                PeakJitterMs = worst.PeakJitterMs
            });
        }
        return merged.OrderBy(e => e.Start).ToList();
    }

    private static bool Overlaps(CongestionEvent a, CongestionEvent b, double minOverlapFraction)
    {
        var overlap = (Min(a.End, b.End) - Max(a.Start, b.Start)).TotalMinutes;
        if (overlap <= 0) return false;
        var shorter = Math.Min(a.Duration.TotalMinutes, b.Duration.TotalMinutes);
        return shorter > 0 && overlap / shorter >= minOverlapFraction;
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    internal static DateTime FloorTime(DateTime time, TimeSpan bucket) =>
        new(time.Ticks - time.Ticks % bucket.Ticks, time.Kind);

    private sealed record Bucket(DateTime Start, double? RttMs, double? P90Ms, double? JitterMs);
}
