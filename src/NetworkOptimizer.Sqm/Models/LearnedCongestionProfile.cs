namespace NetworkOptimizer.Sqm.Models;

/// <summary>
/// A 7-day congestion profile learned from measured throughput on one WAN: per hour of the week,
/// download and upload as a fraction of that link's best hour. Slot index = day * 24 + hour with
/// day 0 = Monday, the same layout the BASELINE arrays in the deployed scripts use.
/// </summary>
public class LearnedCongestionProfile
{
    /// <summary>Hours in a week.</summary>
    public const int Slots = 168;

    /// <summary>Download multipliers (0..1) per hour-of-week slot; the best hour is 1.0.</summary>
    public double[] DownloadMultipliers { get; set; } = Flat();

    /// <summary>Upload multipliers (0..1) per hour-of-week slot; the best hour is 1.0.</summary>
    public double[] UploadMultipliers { get; set; } = Flat();

    /// <summary>Valid samples that landed in each slot (before smoothing).</summary>
    public int[] SampleCounts { get; set; } = new int[Slots];

    /// <summary>Smoothed best-hour download throughput in Mbps; the multipliers are relative to it.</summary>
    public double PeakDownloadMbps { get; set; }

    /// <summary>Smoothed best-hour upload throughput in Mbps; the multipliers are relative to it.</summary>
    public double PeakUploadMbps { get; set; }

    /// <summary>Samples that survived validation and outlier rejection.</summary>
    public int ValidSampleCount { get; set; }

    /// <summary>Distinct calendar days with at least one valid sample.</summary>
    public int DaysSpanned { get; set; }

    /// <summary>Fraction of the 168 slots that hold at least one valid sample of their own.</summary>
    public double Coverage => SampleCounts.Count(c => c > 0) / (double)Slots;

    /// <summary>True once the sample count, coverage, and span are enough to shape from.</summary>
    public bool IsReliable { get; set; }

    /// <summary>Samples that hit the measurement ceiling (the lifted shaper rate) rather than the line.</summary>
    public int ProbeLimitedSampleCount { get; set; }

    /// <summary>
    /// True when every sample hit the measurement ceiling, so the peaks are the ceiling, not the line:
    /// the real best hour is at least this fast.
    /// </summary>
    public bool PeakIsLowerBound { get; set; }

    /// <summary>Slot index for a day of week (0 = Monday) and hour.</summary>
    public static int SlotIndex(int dayOfWeek, int hour) => dayOfWeek * 24 + hour;

    /// <summary>The download curve as the 7x24 matrix <see cref="ConnectionProfile"/> consumes.</summary>
    public double[,] DownloadPattern() => ToPattern(DownloadMultipliers);

    /// <summary>The upload curve as the 7x24 matrix <see cref="ConnectionProfile"/> consumes.</summary>
    public double[,] UploadPattern() => ToPattern(UploadMultipliers);

    /// <summary>
    /// The curve rescaled so the deployed scripts reproduce the learned absolute throughput under the
    /// user's nominal speed: each hour becomes peak * multiplier / nominal, capped at 1.0 (nominal is
    /// the ceiling) and floored so a slot can never shape to nothing. Identity when nominal equals
    /// the learned peak.
    /// </summary>
    public static double[,] ScaledPattern(double[] multipliers, double peakMbps, int nominalMbps)
    {
        var scale = peakMbps > 0 && nominalMbps > 0 ? peakMbps / nominalMbps : 1.0;
        var result = new double[7, 24];
        for (var slot = 0; slot < Slots; slot++)
        {
            var m = slot < multipliers.Length ? multipliers[slot] : 1.0;
            result[slot / 24, slot % 24] = Math.Clamp(m * scale, 0.05, 1.0);
        }
        return result;
    }

    /// <summary>Average multiplier per hour of day across the week, for compact display.</summary>
    public static double[] HourOfDayAverages(double[] multipliers)
    {
        var result = new double[24];
        for (var hour = 0; hour < 24; hour++)
        {
            double sum = 0;
            for (var day = 0; day < 7; day++)
                sum += multipliers[SlotIndex(day, hour)];
            result[hour] = sum / 7.0;
        }
        return result;
    }

    private static double[,] ToPattern(double[] multipliers)
    {
        var result = new double[7, 24];
        for (var slot = 0; slot < Slots; slot++)
            result[slot / 24, slot % 24] = slot < multipliers.Length ? multipliers[slot] : 1.0;
        return result;
    }

    private static double[] Flat() => Enumerable.Repeat(1.0, Slots).ToArray();
}

/// <summary>
/// One measured throughput sample feeding the learner. Day and hour are the gateway's local
/// time at the moment of the test, so slots line up with the clock the deployed scripts read.
/// A probe-limited sample ran into the lifted shaper rate: its figures are lower bounds, so it
/// shapes the curve but never sets the peak.
/// </summary>
public sealed record LearningSample(
    long Id,
    int DayOfWeek,
    int Hour,
    double DownloadMbps,
    double UploadMbps,
    DateTime SampledAt,
    bool ProbeLimited = false);
