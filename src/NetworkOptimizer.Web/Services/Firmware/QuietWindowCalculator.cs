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
    /// Sites with at least this many devices keep Autopilot clear of the start of the business week
    /// (see <see cref="HitsMondayGuard"/>).
    /// </summary>
    public const int MondayGuardMinDevices = 4;

    /// <summary>How far a flexible preferred time may move, in hours either side.</summary>
    public const int FlexibleHours = 3;

    // Hours of the week, Sunday midnight = 0.
    private const int SundayCutoffHour = 17;
    private const int SundayFinishByHour = 19;
    private const int MondayResumeHour = 24 + 9;

    /// <summary>
    /// Whether a rollout starting at this hour of the week runs into the start of the business week:
    /// it starts between Sunday 5 PM and Monday 9 AM, or is still running after Sunday 7 PM. The two
    /// hours past 5 PM let a rollout that is nearly done finish rather than move a whole day.
    /// </summary>
    /// <param name="startHourOfWeek">Start bucket, (int)DayOfWeek * 24 + hour.</param>
    /// <param name="durationSeconds">Estimated rollout length.</param>
    public static bool HitsMondayGuard(int startHourOfWeek, int durationSeconds)
    {
        const double week = BucketsPerWeek * 3600.0;
        var start = startHourOfWeek * 3600.0;
        var end = start + Math.Max(0, durationSeconds);

        // Every Sunday the rollout could reach: the one in its own week and any it runs into.
        for (var k = 0; k * week <= end; k++)
        {
            var cutoff = SundayCutoffHour * 3600.0 + k * week;
            var finishBy = SundayFinishByHour * 3600.0 + k * week;
            var resume = MondayResumeHour * 3600.0 + k * week;

            if (start >= cutoff && start < resume) return true;
            if (start < finishBy && end > finishBy) return true;
        }
        return false;
    }

    /// <summary>
    /// The lowest-scoring window long enough for the rollout. Ties break toward the
    /// soonest occurrence after <paramref name="minLead"/> from <paramref name="nowLocal"/>.
    /// </summary>
    /// <param name="busy168">Hour-of-week busy fingerprint.</param>
    /// <param name="durationSeconds">Estimated rollout length.</param>
    /// <param name="nowLocal">Now, in the site's zone.</param>
    /// <param name="minLead">Least notice the window must leave.</param>
    /// <param name="avoidMondayMorning">Skip every start <see cref="HitsMondayGuard"/> rejects.</param>
    public static QuietWindowProposal FindBest(
        double[] busy168, int durationSeconds, DateTime nowLocal, TimeSpan minLead, bool avoidMondayMorning = false)
    {
        if (busy168.Length != BucketsPerWeek)
            throw new ArgumentException($"Fingerprint must have {BucketsPerWeek} buckets", nameof(busy168));

        var durationBuckets = Math.Max(1, (int)Math.Ceiling(durationSeconds / 3600.0));
        var best = -1;
        var bestScore = double.MaxValue;
        DateTime bestStart = default;

        for (var start = 0; start < BucketsPerWeek; start++)
        {
            if (avoidMondayMorning && HitsMondayGuard(start, durationSeconds)) continue;

            var score = WindowScore(busy168, start, durationBuckets, DaytimePenalty);
            var startTime = NextOccurrence((DayOfWeek)(start / 24), start % 24, nowLocal, minLead);
            if (score < bestScore - 1e-9 ||
                (Math.Abs(score - bestScore) <= 1e-9 && best >= 0 && startTime < bestStart))
            {
                best = start;
                bestScore = score;
                bestStart = startTime;
            }
        }

        // Only a rollout of most of a week has no start clear of the guard; it still needs a window.
        if (best < 0)
            return FindBest(busy168, durationSeconds, nowLocal, minLead, avoidMondayMorning: false);

        var busyMean = MeanBusy(busy168, best, durationBuckets);
        return new QuietWindowProposal
        {
            Day = (DayOfWeek)(best / 24),
            Hour = best % 24,
            StartLocal = bestStart,
            BusyScore = busyMean,
            UsedFallback = false,
            Basis = "4 weeks of usage history",
        };
    }

    /// <summary>
    /// A flexible preferred time: the quietest start within <see cref="FlexibleHours"/> either side
    /// of it, ties going to the hour closest to the preference. The preference itself stands when
    /// no hour in that range passes the guard. No daytime penalty: the user chose the hour.
    /// </summary>
    /// <param name="busy168">Hour-of-week busy fingerprint.</param>
    /// <param name="durationSeconds">Estimated rollout length.</param>
    /// <param name="day">Preferred day.</param>
    /// <param name="hour">Preferred site-local hour.</param>
    /// <param name="nowLocal">Now, in the site's zone.</param>
    /// <param name="minLead">Least notice the window must leave.</param>
    /// <param name="avoidMondayMorning">Skip every start <see cref="HitsMondayGuard"/> rejects.</param>
    public static QuietWindowProposal FindNear(
        double[] busy168, int durationSeconds, DayOfWeek day, int hour, DateTime nowLocal, TimeSpan minLead,
        bool avoidMondayMorning)
    {
        if (busy168.Length != BucketsPerWeek)
            throw new ArgumentException($"Fingerprint must have {BucketsPerWeek} buckets", nameof(busy168));

        var preferred = (int)day * 24 + Math.Clamp(hour, 0, 23);
        var durationBuckets = Math.Max(1, (int)Math.Ceiling(durationSeconds / 3600.0));
        int? best = null;
        var bestScore = double.MaxValue;
        var bestDistance = int.MaxValue;
        DateTime bestStart = default;

        for (var offset = -FlexibleHours; offset <= FlexibleHours; offset++)
        {
            var start = (preferred + offset + BucketsPerWeek) % BucketsPerWeek;
            if (avoidMondayMorning && HitsMondayGuard(start, durationSeconds)) continue;

            var score = WindowScore(busy168, start, durationBuckets, daytimePenalty: 0);
            var distance = Math.Abs(offset);
            var startTime = NextOccurrence((DayOfWeek)(start / 24), start % 24, nowLocal, minLead);
            var better = score < bestScore - 1e-9
                || (Math.Abs(score - bestScore) <= 1e-9
                    && (distance < bestDistance || (distance == bestDistance && startTime < bestStart)));
            if (best == null || better)
            {
                best = start;
                bestScore = score;
                bestDistance = distance;
                bestStart = startTime;
            }
        }

        if (best is not int chosen)
            return Fixed(day, hour, nowLocal, minLead);

        return new QuietWindowProposal
        {
            Day = (DayOfWeek)(chosen / 24),
            Hour = chosen % 24,
            StartLocal = bestStart,
            BusyScore = MeanBusy(busy168, chosen, durationBuckets),
            UsedFallback = false,
            Basis = chosen == preferred ? "your preferred time" : "your preferred time, moved to a quieter hour nearby",
        };
    }

    /// <summary>Mean busy fraction over the window, plus the daytime penalty for each daytime hour.</summary>
    private static double WindowScore(double[] busy168, int start, int buckets, double daytimePenalty)
    {
        double score = 0;
        for (var i = 0; i < buckets; i++)
        {
            var b = (start + i) % BucketsPerWeek;
            var hour = b % 24;
            score += busy168[b];
            if (hour >= DaytimeStartHour && hour < DaytimeEndHour) score += daytimePenalty;
        }
        return score / buckets;
    }

    /// <summary>Default window when no usable history exists.</summary>
    public static QuietWindowProposal Fallback(SiteUsageProfile profile, DateTime nowLocal, TimeSpan minLead)
    {
        // Home networks are quietest on weekday small hours; businesses on weekend early
        // mornings, before opening and clear of Friday-night batch work. Saturday rather than
        // Sunday leaves a full day to catch a problem before the business week starts.
        var (day, hour, basis) = profile == SiteUsageProfile.Business
            ? (DayOfWeek.Saturday, 4, "business-profile default (weekend early morning)")
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
