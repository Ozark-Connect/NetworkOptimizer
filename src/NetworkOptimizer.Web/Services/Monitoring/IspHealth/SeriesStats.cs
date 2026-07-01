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

    // ---- Pre-sorted variants ----
    // When several statistics are needed over the same series, the caller sorts once and uses these
    // instead of the list overloads (each of which sorts internally). Results are identical.

    /// <summary>Percentile of an already ascending-sorted list, matching <see cref="Percentile"/>.</summary>
    public static double? PercentileSorted(IReadOnlyList<double> sortedAsc, double p)
    {
        if (sortedAsc.Count == 0) return null;
        var rank = p * (sortedAsc.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sortedAsc[lo];
        return sortedAsc[lo] + (sortedAsc[hi] - sortedAsc[lo]) * (rank - lo);
    }

    /// <summary>Median of an already ascending-sorted list, matching <see cref="Median"/>.</summary>
    public static double? MedianSorted(IReadOnlyList<double> sortedAsc) => PercentileSorted(sortedAsc, 0.5);

    /// <summary>
    /// MAD given the series already ascending-sorted and its (pre-computed) median, so the median
    /// isn't re-sorted for. Identical result to <see cref="Mad"/>.
    /// </summary>
    public static double? MadSorted(IReadOnlyList<double> sortedAsc, double median)
    {
        if (sortedAsc.Count == 0) return null;
        var dev = new double[sortedAsc.Count];
        for (var i = 0; i < sortedAsc.Count; i++) dev[i] = Math.Abs(sortedAsc[i] - median);
        Array.Sort(dev);
        return PercentileSorted(dev, 0.5);
    }

    /// <summary>Winsorized mean of an already ascending-sorted list, matching <see cref="WinsorizedMean"/>.</summary>
    public static double? WinsorizedMeanSorted(IReadOnlyList<double> sortedAsc, double upperPercentile)
    {
        if (sortedAsc.Count == 0) return null;
        var cap = PercentileSorted(sortedAsc, upperPercentile);
        if (cap == null) return null;
        double sum = 0;
        for (var i = 0; i < sortedAsc.Count; i++) sum += Math.Min(sortedAsc[i], cap.Value);
        return sum / sortedAsc.Count;
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
