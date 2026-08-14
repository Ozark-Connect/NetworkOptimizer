namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// Pure quiet-window selection over a 168-bucket hour-of-week busy fingerprint
/// (index = (int)DayOfWeek * 24 + hour, site-local). Values are busy fractions 0..1.
/// </summary>
public static class QuietWindowCalculator
{
    public const int BucketsPerWeek = 168;

    /// <summary>Flat score penalty per daytime bucket so ties break toward overnight windows.</summary>
    public const double DaytimePenalty = 0.15;

    /// <summary>Local hours considered daytime for the sane-hours preference.</summary>
    public const int DaytimeStartHour = 7;
    public const int DaytimeEndHour = 22;

    /// <summary>
    /// The lowest-scoring window long enough for the rollout. Ties break toward the
    /// soonest occurrence after <paramref name="minLead"/> from <paramref name="nowLocal"/>.
    /// </summary>
    public static QuietWindowProposal FindBest(double[] busy168, int durationSeconds, DateTime nowLocal, TimeSpan minLead)
    {
        if (busy168.Length != BucketsPerWeek)
            throw new ArgumentException($"Fingerprint must have {BucketsPerWeek} buckets", nameof(busy168));

        var durationBuckets = Math.Max(1, (int)Math.Ceiling(durationSeconds / 3600.0));
        var best = -1;
        var bestScore = double.MaxValue;
        DateTime bestStart = default;

        for (var start = 0; start < BucketsPerWeek; start++)
        {
            double score = 0;
            for (var i = 0; i < durationBuckets; i++)
            {
                var b = (start + i) % BucketsPerWeek;
                var hour = b % 24;
                score += busy168[b];
                if (hour >= DaytimeStartHour && hour < DaytimeEndHour) score += DaytimePenalty;
            }
            score /= durationBuckets;

            var startTime = NextOccurrence((DayOfWeek)(start / 24), start % 24, nowLocal, minLead);
            if (score < bestScore - 1e-9 ||
                (Math.Abs(score - bestScore) <= 1e-9 && best >= 0 && startTime < bestStart))
            {
                best = start;
                bestScore = score;
                bestStart = startTime;
            }
        }

        var busyMean = MeanBusy(busy168, best, durationBuckets);
        return new QuietWindowProposal
        {
            Day = (DayOfWeek)(best / 24),
            Hour = best % 24,
            StartLocal = bestStart,
            BusyScore = busyMean,
            UsedFallback = false,
            Basis = "7-day usage history",
        };
    }

    /// <summary>Default window when no usable history exists.</summary>
    public static QuietWindowProposal Fallback(SiteUsageProfile profile, DateTime nowLocal, TimeSpan minLead)
    {
        // Home networks are quietest on weekday small hours; businesses on weekend early
        // mornings, before opening and clear of Friday-night batch work.
        var (day, hour, basis) = profile == SiteUsageProfile.Business
            ? (DayOfWeek.Sunday, 4, "business-profile default (weekend early morning)")
            : (DayOfWeek.Tuesday, 3, "home-profile default (weekday overnight)");

        return new QuietWindowProposal
        {
            Day = day,
            Hour = hour,
            StartLocal = NextOccurrence(day, hour, nowLocal, minLead),
            BusyScore = 0,
            UsedFallback = true,
            Basis = basis,
        };
    }

    /// <summary>A user-pinned window (Fixed autopilot mode).</summary>
    public static QuietWindowProposal Fixed(DayOfWeek day, int hour, DateTime nowLocal, TimeSpan minLead) => new()
    {
        Day = day,
        Hour = Math.Clamp(hour, 0, 23),
        StartLocal = NextOccurrence(day, Math.Clamp(hour, 0, 23), nowLocal, minLead),
        BusyScore = 0,
        UsedFallback = false,
        Basis = "pinned day and hour",
    };

    /// <summary>Next site-local occurrence of (day, hour) at least minLead from now.</summary>
    public static DateTime NextOccurrence(DayOfWeek day, int hour, DateTime nowLocal, TimeSpan minLead)
    {
        var earliest = nowLocal + minLead;
        var candidate = new DateTime(earliest.Year, earliest.Month, earliest.Day, hour, 0, 0, earliest.Kind);
        var dayDelta = ((int)day - (int)candidate.DayOfWeek + 7) % 7;
        candidate = candidate.AddDays(dayDelta);
        if (candidate < earliest) candidate = candidate.AddDays(7);
        return candidate;
    }

    private static double MeanBusy(double[] busy168, int start, int buckets)
    {
        double sum = 0;
        for (var i = 0; i < buckets; i++) sum += busy168[(start + i) % BucketsPerWeek];
        return sum / buckets;
    }
}

/// <summary>
/// Home-vs-business classification for the no-history fallback, from fleet shape alone.
/// Thresholds are deliberate constants: a home rarely exceeds a handful of infrastructure
/// devices, and multi-AP multi-switch fleets or large client counts read as business.
/// </summary>
public static class SiteProfileClassifier
{
    public const int BusinessInfraDeviceThreshold = 12;
    public const int BusinessClientThreshold = 40;
    public const int BusinessApThreshold = 4;
    public const int BusinessSwitchThreshold = 2;

    public static SiteUsageProfile Classify(int infraDeviceCount, int apCount, int switchCount, int clientCount)
    {
        if (infraDeviceCount >= BusinessInfraDeviceThreshold) return SiteUsageProfile.Business;
        if (clientCount >= BusinessClientThreshold) return SiteUsageProfile.Business;
        if (apCount >= BusinessApThreshold && switchCount >= BusinessSwitchThreshold) return SiteUsageProfile.Business;
        return SiteUsageProfile.Home;
    }
}
