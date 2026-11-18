namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// One latency figure for a group of hops, as the Transit RTT tile reads them: the mean of the hops'
/// own baselines, plus the weighted mean of how far each hop sits above its baseline. A hop's excess
/// is the median of its last <see cref="RecentReadings"/> readings, so one slow ICMP reply drops out
/// while a sustained elevation on any hop still shows, in proportion to the hops it reaches. A hop's
/// weight is the inverse of its own jitter, so a persistently noisy hop counts less. The hops'
/// different distances cancel out of the excess, so the figure does not step when a different hop
/// reads lowest.
/// </summary>
public static class HopLatencyBlend
{
    /// <summary>Readings a hop needs before its baseline is trusted.</summary>
    public const int MinHistory = 10;

    /// <summary>Recent readings whose median is a hop's current value: a lone spike is outvoted, two in a row are not.</summary>
    public const int RecentReadings = 3;

    /// <summary>Added to each hop's jitter before weighting, so a hop that reads flat cannot take all the weight.</summary>
    public const double JitterFloorMs = 0.5;

    /// <summary>A hop's baseline (median RTT) and weight (inverse of its median absolute deviation).</summary>
    public readonly record struct Profile(double Baseline, double Weight);

    /// <summary>The profile of a hop's RTT history, or null with fewer than <see cref="MinHistory"/> readings.</summary>
    public static Profile? ProfileOf(IReadOnlyList<double> history)
    {
        if (history.Count < MinHistory) return null;
        var baseline = SeriesStats.Median(history)!.Value;
        var mad = SeriesStats.Mad(history)!.Value;
        return new Profile(baseline, 1.0 / (mad + JitterFloorMs));
    }

    /// <summary>
    /// The figure at one instant. Each hop is its RTT history, oldest first, ending with its current
    /// reading. With no hop settled yet, the mean of the hops' current values. Null with no hop.
    /// </summary>
    public static double? Live(IReadOnlyList<IReadOnlyList<double>> hops) =>
        Blend(hops.Where(h => h.Count > 0)
            .Select(h => (SeriesStats.Median(h.TakeLast(RecentReadings).ToList())!.Value, ProfileOf(h)))
            .ToList());

    /// <summary>
    /// The figure as a series, from hop series that share aggregate-window timestamps. Each hop's
    /// profile comes from its own series in the window. A window's mean already outvotes a lone
    /// spike, so each timestamp blends the hops' window values directly.
    /// </summary>
    public static List<(DateTime Time, double Value)> Series(IReadOnlyList<IReadOnlyList<(DateTime Time, double Value)>> hops)
    {
        var profiles = hops.Select(h => ProfileOf(h.Select(p => p.Value).ToList())).ToList();
        return hops
            .SelectMany((h, i) => h.Select(p => (p.Time, p.Value, Profile: profiles[i])))
            .GroupBy(p => p.Time)
            .OrderBy(g => g.Key)
            .Select(g => (Time: g.Key, Value: Blend(g.Select(p => (p.Value, p.Profile)).ToList())))
            .Where(p => p.Value.HasValue)
            .Select(p => (p.Time, p.Value!.Value))
            .ToList();
    }

    private static double? Blend(IReadOnlyList<(double Rtt, Profile? Profile)> hops)
    {
        if (hops.Count == 0) return null;
        // A hop without a profile yet stands at its current value in the level, so the level does
        // not step each time another hop settles.
        var level = hops.Average(h => h.Profile?.Baseline ?? h.Rtt);
        var settled = hops.Where(h => h.Profile.HasValue).ToList();
        if (settled.Count == 0) return level;
        var weight = settled.Sum(h => h.Profile!.Value.Weight);
        return level + settled.Sum(h => (h.Rtt - h.Profile!.Value.Baseline) * h.Profile.Value.Weight) / weight;
    }
}
