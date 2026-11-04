namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// The access layer's latency as one series: at each timestamp, the lowest RTT any access hop
/// reported. A queue on the access link sits in front of every access hop, so congestion lifts all
/// of them and the floor with them; one hop's slow control-plane replies lift only that hop and
/// lose to the others.
/// </summary>
public static class AccessFloor
{
    /// <summary>Hops that must report a timestamp before the floor is taken there.</summary>
    public const int MinTargets = 2;

    /// <summary>
    /// Builds the floor from the access hops' series, which share aggregate-window timestamps.
    /// Where fewer than <paramref name="minTargets"/> hops report an RTT, the timestamp takes
    /// <paramref name="fallback"/>'s sample (the single lowest-median hop, the old behavior), so a
    /// hop going dark never moves the floor by more than that hop's own share.
    /// </summary>
    /// <param name="series">One series per access hop.</param>
    /// <param name="fallback">The lowest-median hop's series; also the source of each sample's loss.</param>
    /// <param name="minTargets">Hops that must report a timestamp for the floor to be taken.</param>
    public static List<LatencySample> Build(
        IReadOnlyList<IReadOnlyList<LatencySample>> series,
        IReadOnlyList<LatencySample> fallback,
        int minTargets = MinTargets)
    {
        if (series.Count(s => s.Count > 0) < minTargets) return fallback.ToList();

        var fallbackByTime = new Dictionary<DateTime, LatencySample>();
        foreach (var s in fallback) fallbackByTime[s.Time] = s;

        var result = new List<LatencySample>();
        foreach (var group in series.SelectMany(s => s).GroupBy(s => s.Time).OrderBy(g => g.Key))
        {
            var reporting = group.Where(s => s.RttAvgMs.HasValue).ToList();
            fallbackByTime.TryGetValue(group.Key, out var own);
            if (reporting.Count < minTargets)
            {
                if (own != null) result.Add(own);
                continue;
            }

            var lowest = reporting.MinBy(s => s.RttAvgMs!.Value)!;
            var jitter = reporting.Where(s => s.JitterMs.HasValue).Select(s => s.JitterMs!.Value).DefaultIfEmpty(double.NaN).Min();
            result.Add(new LatencySample(group.Key, lowest.RttAvgMs, lowest.RttMaxMs,
                double.IsNaN(jitter) ? null : jitter, own?.LossPercent));
        }
        return result;
    }
}
