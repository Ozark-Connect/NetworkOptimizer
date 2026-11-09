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

    /// <summary>
    /// The floor's loss as a series for display: the lowest-median hop's loss, as in
    /// <see cref="Build"/>, and where that hop has no reading at a timestamp, the mean loss of
    /// the hops that do. Build leaves those timestamps without loss, which ISP Health's scoring
    /// expects; a chart drawn from it lost its loss line wherever that one hop was missing (a hop
    /// newer than the window, most often).
    /// </summary>
    public static List<(DateTime Time, double LossPercent)> LossSeries(
        IReadOnlyList<IReadOnlyList<LatencySample>> series,
        IReadOnlyList<LatencySample> fallback)
    {
        var own = new Dictionary<DateTime, double>();
        foreach (var s in fallback)
            if (s.LossPercent is double l) own[s.Time] = l;

        return series.SelectMany(s => s)
            .Where(s => s.LossPercent.HasValue)
            .GroupBy(s => s.Time)
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, own.TryGetValue(g.Key, out var l) ? l : g.Average(s => s.LossPercent!.Value)))
            .ToList();
    }

    /// <summary>
    /// The floor at one instant, from each access hop's latest reading: the live ISP tiles and the
    /// WAN live chart's tick, so they read the access layer the way <see cref="Build"/> does.
    /// With <paramref name="minTargets"/> or more hops reporting an RTT, the lowest of them, with
    /// loss from <paramref name="lowestMedianId"/> (the hop <see cref="Build"/> is given as its
    /// fallback). With fewer, that hop's own reading. When it is not reporting either, what did
    /// report stands in, and with no RTT anywhere the mean loss: no reading is not the same claim
    /// as no loss, and reading 0% through an outage is the error this keeps out.
    /// </summary>
    /// <param name="fresh">Each hop's current reading; stale hops are left out by the caller.</param>
    /// <param name="lowestMedianId">The hop with the lowest median RTT over the recent hour, if known.</param>
    public static (double? RttAvgMs, double? LossPercent) Live(
        IReadOnlyList<(string Id, double? RttAvgMs, double LossPercent)> fresh,
        string? lowestMedianId,
        int minTargets = MinTargets)
    {
        if (fresh.Count == 0) return (null, null);
        var own = fresh.Where(f => f.Id == lowestMedianId).Select(f => ((double? Rtt, double Loss)?)(f.RttAvgMs, f.LossPercent)).FirstOrDefault();
        var reporting = fresh.Where(f => f.RttAvgMs.HasValue).OrderBy(f => f.RttAvgMs!.Value).ToList();

        if (reporting.Count >= minTargets)
            return (reporting[0].RttAvgMs, own?.Loss ?? reporting[0].LossPercent);
        if (own is { Rtt: not null } o)
            return (o.Rtt, o.Loss);
        return reporting.Count > 0
            ? (reporting[0].RttAvgMs, reporting[0].LossPercent)
            : (null, fresh.Average(f => f.LossPercent));
    }
}
