namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// Robust receive-power statistics for optical (SFP/ONT) DDM samples, with DDM read-artifact
/// rejection. Cheap SFP DDM sticks occasionally return a garbage read where several fields glitch
/// together in a single sample - RX dives while the reported temperature jumps tens of degrees,
/// which is physically impossible between polls. Temperature is NOT a health metric here; it is the
/// tell that flags and discards the bad read so it can't drag the RX statistics the score rides on.
/// </summary>
public static class OpticalSampleStats
{
    /// <summary>
    /// A sample whose temperature deviates from the window median by more than this (C) is treated
    /// as a DDM read artifact and dropped. Real thermal drift is gradual and small between polls;
    /// the artifacts seen in the field jump 20-30+ C in one sample.
    /// </summary>
    public const double DdmTempArtifactDeltaC = 12.0;

    /// <summary>Robust RX summary over a window after artifact rejection.</summary>
    public sealed record RxStats(double? MedianDbm, double? WorstDbm, double? BaselineDbm, int CleanCount, int RejectedArtifacts);

    /// <summary>
    /// Computes median / worst / baseline RX over the samples, dropping DDM artifacts first.
    /// Worst is the coldest CLEAN sample; baseline is the median of the earliest fifth (for the
    /// trend check). Samples missing RX are ignored; samples missing temperature can't be judged
    /// and are kept.
    /// </summary>
    public static RxStats Compute(IReadOnlyList<(DateTime Time, double? Rx, double? Temp)> samples)
    {
        var medianTemp = Median(samples.Where(s => s.Temp.HasValue).Select(s => s.Temp!.Value).ToList());

        var rejected = 0;
        var clean = new List<double>();
        foreach (var s in samples.OrderBy(s => s.Time))
        {
            if (s.Rx is not double rx) continue;
            if (medianTemp is double mt && s.Temp is double t && Math.Abs(t - mt) > DdmTempArtifactDeltaC)
            {
                rejected++;
                continue;
            }
            clean.Add(rx);
        }

        return new RxStats(Median(clean), clean.Count > 0 ? clean.Min() : null, Baseline(clean), clean.Count, rejected);
    }

    internal static double? Median(List<double> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>Median of the earliest fifth of the (time-ordered) clean samples - the trend baseline. Null when too sparse.</summary>
    internal static double? Baseline(List<double> timeOrdered)
    {
        if (timeOrdered.Count < 5) return null;
        var take = Math.Max(1, timeOrdered.Count / 5);
        return Median(timeOrdered.Take(take).ToList());
    }
}
