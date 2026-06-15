namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// Robust statistics over latency series. Local to ISP Health for now; promote to
/// Core/Helpers if another subsystem needs medians or MADs.
/// </summary>
internal static class SeriesStats
{
    public static double? Median(IReadOnlyList<double> values) => Percentile(values, 0.5);

    public static double? Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToArray();
        var rank = p * (sorted.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sorted[lo];
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    /// <summary>
    /// Mean after winsorizing the upper tail: values above the given percentile are capped
    /// to it, then averaged. Keeps sustained elevation fully visible (those samples sit
    /// below the cap) while stopping a few extreme outliers - a route flap or a single bad
    /// probe - from dragging the average. Null when empty.
    /// </summary>
    public static double? WinsorizedMean(IReadOnlyList<double> values, double upperPercentile)
    {
        if (values.Count == 0) return null;
        var cap = Percentile(values, upperPercentile);
        if (cap == null) return values.Average();
        return values.Select(v => Math.Min(v, cap.Value)).Average();
    }

    /// <summary>Median absolute deviation; robust spread measure.</summary>
    public static double? Mad(IReadOnlyList<double> values)
    {
        var median = Median(values);
        if (median == null) return null;
        var deviations = values.Select(v => Math.Abs(v - median.Value)).ToList();
        return Median(deviations);
    }

    /// <summary>Interquartile range as (q1, q3), or null when empty.</summary>
    public static (double Q1, double Q3)? Iqr(IReadOnlyList<double> values)
    {
        var q1 = Percentile(values, 0.25);
        var q3 = Percentile(values, 0.75);
        if (q1 == null || q3 == null) return null;
        return (q1.Value, q3.Value);
    }
}
