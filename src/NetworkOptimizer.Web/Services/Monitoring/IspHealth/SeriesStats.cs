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
    /// Median with each value weighted, taken at the point where half the total weight has
    /// accumulated. Still a median - one wild value cannot drag it the way a weighted mean can -
    /// but a heavier sample counts for more of the half.
    /// <para>
    /// Used where recent evidence should outrank old evidence of the same kind: a plain median
    /// over a week-long window treats a measurement from an hour ago exactly like one from six
    /// days ago, so a line that was fixed this afternoon keeps reporting the fault until the good
    /// samples outnumber the bad ones.
    /// </para>
    /// </summary>
    public static double? WeightedMedian(IReadOnlyList<(double Value, double Weight)> samples)
    {
        var usable = samples.Where(s => s.Weight > 0).OrderBy(s => s.Value).ToArray();
        if (usable.Length == 0) return null;

        var half = usable.Sum(s => s.Weight) / 2.0;
        var running = 0.0;
        foreach (var (value, weight) in usable)
        {
            running += weight;
            if (running >= half) return value;
        }
        return usable[^1].Value;
    }

    /// <summary>
    /// Weighted arithmetic mean. Used where the quantity is naturally averaged - loss is a rate,
    /// and a median over mostly-zero samples reports zero however bad the rest are.
    /// </summary>
    public static double? WeightedMean(IReadOnlyList<(double Value, double Weight)> samples)
    {
        var total = 0.0;
        var weight = 0.0;
        foreach (var (value, w) in samples)
        {
            if (w <= 0) continue;
            total += value * w;
            weight += w;
        }
        return weight > 0 ? total / weight : null;
    }

    /// <summary>
    /// How long the run of consecutive loaded windows containing each window lasted, in seconds.
    /// <para>
    /// Duration is credibility, not just sample count. A short burst is where load classification
    /// goes wrong most often, and it is too brief for buffers to fill, so its latency understates
    /// what a full pipe does - weak evidence twice over. A long saturation is the best evidence
    /// there is, better than a speed test, which is itself short and synthetic.
    /// </para>
    /// </summary>
    public static Dictionary<DateTime, double> LoadEpisodeSeconds(
        IEnumerable<DateTime> loadedWindowKeys, int windowSeconds)
    {
        var size = Math.Max(1, windowSeconds);
        var ordered = loadedWindowKeys.Distinct().OrderBy(t => t).ToList();
        var seconds = new Dictionary<DateTime, double>();
        for (var i = 0; i < ordered.Count;)
        {
            var run = 1;
            while (i + run < ordered.Count
                && (ordered[i + run] - ordered[i + run - 1]).TotalSeconds <= size + 0.001)
            {
                run++;
            }
            var episode = run * (double)size;
            for (var j = 0; j < run; j++) seconds[ordered[i + j]] = episode;
            i += run;
        }
        return seconds;
    }

    /// <summary>
    /// The start time of the run of consecutive loaded windows each window belongs to, so samples
    /// can be grouped by EPISODE rather than by window. A window is seven seconds; an episode is
    /// however long the line actually stayed loaded, which is the unit a person would call "a load
    /// event" and the only one at which "the last three" means anything.
    /// </summary>
    public static Dictionary<DateTime, DateTime> LoadEpisodeStarts(
        IEnumerable<DateTime> loadedWindowKeys, int windowSeconds)
    {
        var size = Math.Max(1, windowSeconds);
        var ordered = loadedWindowKeys.Distinct().OrderBy(t => t).ToList();
        var starts = new Dictionary<DateTime, DateTime>();
        for (var i = 0; i < ordered.Count;)
        {
            var run = 1;
            while (i + run < ordered.Count
                && (ordered[i + run] - ordered[i + run - 1]).TotalSeconds <= size + 0.001)
            {
                run++;
            }
            for (var j = 0; j < run; j++) starts[ordered[i + j]] = ordered[i];
            i += run;
        }
        return starts;
    }

    /// <summary>
    /// A credibility multiplier that rises to 1 as a measure approaches the level at which it is
    /// fully believable, and never falls below <paramref name="floor"/> - weak evidence is not
    /// absent evidence. A non-positive target means "cannot judge", which is 1 throughout.
    /// </summary>
    public static double Credibility(double measured, double fullAt, double floor)
        => fullAt <= 0 ? 1 : Math.Clamp(measured / fullAt, floor, 1);

    /// <summary>
    /// The same over a BAND: nothing earned below <paramref name="start"/>, everything earned at
    /// <paramref name="fullAt"/>. For measures whose interesting range does not begin at zero - a
    /// ramp from zero would score every value near the top and separate nothing.
    /// </summary>
    public static double CredibilityBetween(double measured, double start, double fullAt, double floor)
    {
        var span = fullAt - start;
        return span <= 0
            ? Credibility(measured, fullAt, floor)
            : Math.Clamp((measured - start) / span, floor, 1);
    }

    /// <summary>
    /// Weight for a sample of a given age, halving every <paramref name="halfLifeHours"/>. Zero or
    /// negative half-life means no decay at all, which is how a caller opts out.
    /// </summary>
    public static double RecencyWeight(TimeSpan age, double halfLifeHours)
    {
        if (halfLifeHours <= 0) return 1;
        var hours = Math.Max(0, age.TotalHours);
        return Math.Pow(0.5, hours / halfLifeHours);
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
