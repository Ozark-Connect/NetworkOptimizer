using NetworkOptimizer.Sqm.Models;

namespace NetworkOptimizer.Sqm;

/// <summary>
/// Turns a week of hourly throughput samples into a <see cref="LearnedCongestionProfile"/>.
/// Rejects errant samples (a download that was running, a bad server), then smooths over the
/// 168-hour ring so a single noisy sample cannot carve a hole into the schedule. Deterministic:
/// the same samples always yield the same profile.
/// </summary>
public static class CongestionProfileLearner
{
    /// <summary>Valid samples needed before a profile is called reliable.</summary>
    public const int MinReliableSamples = 100;

    /// <summary>Fraction of the 168 slots that need a sample of their own for a reliable profile.</summary>
    public const double MinReliableCoverage = 0.70;

    /// <summary>Distinct days a reliable profile must span.</summary>
    public const int MinReliableDays = 6;

    /// <summary>Samples in an hour-of-day pool before outlier rejection is attempted.</summary>
    public const int MinPoolForOutliers = 4;

    /// <summary>A sample below this fraction of its hour's median is treated as errant (busy link, bad server).</summary>
    public const double LowOutlierRatio = 0.45;

    /// <summary>A sample above this multiple of its hour's median is treated as errant.</summary>
    public const double HighOutlierRatio = 1.8;

    /// <summary>Floor for any slot's multiplier, so the schedule never collapses a link to nothing.</summary>
    public const double MinMultiplier = 0.2;

    // Smoothing weights over the hour-of-week ring. With one week of hourly samples every slot
    // holds a single reading; its own value keeps the largest say, the adjacent hours of the same
    // day carry the shape of that day, and the same hour on other days anchors the time-of-day.
    private const double OwnWeight = 1.0;
    private const double AdjacentHourWeight = 0.5;
    private const double SameHourOtherDayWeight = 0.25;
    private const double AdjacentHourOtherDayWeight = 0.1;

    /// <summary>A sample the learner left out, and why.</summary>
    public sealed record Exclusion(long SampleId, string Reason);

    /// <summary>The learned profile plus the samples that were excluded on the way.</summary>
    public sealed record Result(LearnedCongestionProfile Profile, IReadOnlyList<Exclusion> Exclusions);

    /// <summary>Learns a profile from raw samples.</summary>
    public static Result Learn(IReadOnlyCollection<LearningSample> samples)
    {
        var exclusions = new List<Exclusion>();
        var valid = new List<LearningSample>();
        foreach (var s in samples)
        {
            if (s.DayOfWeek is < 0 or > 6 || s.Hour is < 0 or > 23)
                exclusions.Add(new Exclusion(s.Id, "invalid time slot"));
            else if (s.DownloadMbps <= 0 || s.UploadMbps <= 0)
                exclusions.Add(new Exclusion(s.Id, "no throughput measured"));
            else
                valid.Add(s);
        }

        var kept = new List<LearningSample>();
        foreach (var pool in valid.GroupBy(s => s.Hour))
        {
            var list = pool.ToList();
            if (list.Count < MinPoolForOutliers)
            {
                kept.AddRange(list);
                continue;
            }

            var medianDown = Median(list.Select(s => s.DownloadMbps));
            var medianUp = Median(list.Select(s => s.UploadMbps));
            foreach (var s in list)
            {
                if (IsOutlier(s.DownloadMbps, medianDown))
                    exclusions.Add(new Exclusion(s.Id,
                        $"download {s.DownloadMbps:F0} Mbps against {medianDown:F0} Mbps typical at {s.Hour:00}:00"));
                else if (IsOutlier(s.UploadMbps, medianUp))
                    exclusions.Add(new Exclusion(s.Id,
                        $"upload {s.UploadMbps:F0} Mbps against {medianUp:F0} Mbps typical at {s.Hour:00}:00"));
                else
                    kept.Add(s);
            }
        }

        var profile = new LearnedCongestionProfile { ValidSampleCount = kept.Count };
        if (kept.Count == 0)
            return new Result(profile, exclusions);

        var counts = new int[LearnedCongestionProfile.Slots];
        var sumDown = new double[LearnedCongestionProfile.Slots];
        var sumUp = new double[LearnedCongestionProfile.Slots];
        foreach (var s in kept)
        {
            var slot = LearnedCongestionProfile.SlotIndex(s.DayOfWeek, s.Hour);
            counts[slot]++;
            sumDown[slot] += s.DownloadMbps;
            sumUp[slot] += s.UploadMbps;
        }

        var smoothDown = Smooth(sumDown, counts, HourOfDayMeans(kept, s => s.DownloadMbps), Median(kept.Select(s => s.DownloadMbps)));
        var smoothUp = Smooth(sumUp, counts, HourOfDayMeans(kept, s => s.UploadMbps), Median(kept.Select(s => s.UploadMbps)));

        // The peak comes only from samples that measured the line. A probe-limited sample is a
        // lower bound: it may pull the shape, but it must not become the ceiling everything is
        // relative to, or the learned nominal is our own lift rate.
        var unclipped = kept.Where(s => !s.ProbeLimited).ToList();
        double peakDown, peakUp;
        if (unclipped.Count > 0)
        {
            var uCounts = new int[LearnedCongestionProfile.Slots];
            var uDown = new double[LearnedCongestionProfile.Slots];
            var uUp = new double[LearnedCongestionProfile.Slots];
            foreach (var s in unclipped)
            {
                var slot = LearnedCongestionProfile.SlotIndex(s.DayOfWeek, s.Hour);
                uCounts[slot]++;
                uDown[slot] += s.DownloadMbps;
                uUp[slot] += s.UploadMbps;
            }
            peakDown = Smooth(uDown, uCounts, HourOfDayMeans(unclipped, s => s.DownloadMbps), Median(unclipped.Select(s => s.DownloadMbps))).Max();
            peakUp = Smooth(uUp, uCounts, HourOfDayMeans(unclipped, s => s.UploadMbps), Median(unclipped.Select(s => s.UploadMbps))).Max();
        }
        else
        {
            peakDown = smoothDown.Max();
            peakUp = smoothUp.Max();
            profile.PeakIsLowerBound = true;
        }

        profile.SampleCounts = counts;
        profile.DownloadMultipliers = smoothDown.Select(v => Normalize(v, peakDown)).ToArray();
        profile.UploadMultipliers = smoothUp.Select(v => Normalize(v, peakUp)).ToArray();
        profile.PeakDownloadMbps = Math.Round(peakDown, 1);
        profile.PeakUploadMbps = Math.Round(peakUp, 1);
        profile.ProbeLimitedSampleCount = kept.Count - unclipped.Count;
        profile.DaysSpanned = kept.Select(s => s.SampledAt.Date).Distinct().Count();
        profile.IsReliable = kept.Count >= MinReliableSamples
            && profile.Coverage >= MinReliableCoverage
            && profile.DaysSpanned >= MinReliableDays;

        return new Result(profile, exclusions);
    }

    private static bool IsOutlier(double value, double median) =>
        median > 0 && (value < median * LowOutlierRatio || value > median * HighOutlierRatio);

    private static double Normalize(double value, double peak) =>
        peak <= 0 ? 1.0 : Math.Round(Math.Clamp(value / peak, MinMultiplier, 1.0), 4);

    /// <summary>
    /// Weighted mean over the ring for every slot; slots with no neighbours at all fall back to
    /// the hour-of-day mean, then to the overall median.
    /// </summary>
    private static double[] Smooth(double[] sums, int[] counts, double?[] hourOfDayMeans, double overallMedian)
    {
        const int slots = LearnedCongestionProfile.Slots;
        var result = new double[slots];
        for (var slot = 0; slot < slots; slot++)
        {
            var day = slot / 24;
            var hour = slot % 24;
            double weight = 0, total = 0;

            void Add(int s, double w)
            {
                if (counts[s] == 0) return;
                weight += w * counts[s];
                total += w * sums[s];
            }

            Add(slot, OwnWeight);
            Add((slot + 1) % slots, AdjacentHourWeight);
            Add((slot + slots - 1) % slots, AdjacentHourWeight);
            for (var otherDay = 0; otherDay < 7; otherDay++)
            {
                if (otherDay == day) continue;
                Add(LearnedCongestionProfile.SlotIndex(otherDay, hour), SameHourOtherDayWeight);
                Add(LearnedCongestionProfile.SlotIndex(otherDay, (hour + 1) % 24), AdjacentHourOtherDayWeight);
                Add(LearnedCongestionProfile.SlotIndex(otherDay, (hour + 23) % 24), AdjacentHourOtherDayWeight);
            }

            result[slot] = weight > 0 ? total / weight : hourOfDayMeans[hour] ?? overallMedian;
        }
        return result;
    }

    private static double?[] HourOfDayMeans(List<LearningSample> samples, Func<LearningSample, double> selector)
    {
        var result = new double?[24];
        foreach (var pool in samples.GroupBy(s => s.Hour))
            result[pool.Key] = pool.Average(selector);
        return result;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
    }
}
