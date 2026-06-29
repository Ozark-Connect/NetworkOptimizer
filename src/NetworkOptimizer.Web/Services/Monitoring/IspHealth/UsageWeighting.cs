namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// Time-of-day usage weighting for outage severity. The service builds an hour-of-day fingerprint
/// (per local hour, the fraction of time the WAN was actively in use) from throughput we already
/// record; this pure helper turns that fingerprint plus an event's local hours into a 0..1 weight,
/// normalized against the busiest hour and floored. An outage during the user's heavy-usage hours
/// weighs in full; one during typically-idle hours weighs less, never below the floor (an outage is
/// still an outage). All the timezone/Influx work lives in the service - this stays pure and testable.
/// </summary>
public static class UsageWeighting
{
    /// <summary>
    /// Weight for an event covering <paramref name="localHours"/>, given the hour-of-day active
    /// fraction profile. Normalized to the profile's peak hour so a lightly-used line still weighs its
    /// own busiest hour at 1.0, then clamped to [<paramref name="floor"/>, 1]. Returns 1.0 (no
    /// softening) when the profile is absent/degenerate or no hours are given.
    /// </summary>
    public static double Weight(double[]? hourlyActiveFraction, IReadOnlyCollection<int> localHours, double floor)
    {
        if (hourlyActiveFraction is not { Length: 24 } profile || localHours.Count == 0)
            return 1.0;

        var peak = profile.Max();
        if (peak <= 0) return 1.0; // line never registered active anywhere - nothing to normalize against

        var frac = localHours.Average(h => profile[h]);
        var weight = floor + (1.0 - floor) * (frac / peak);
        return Math.Clamp(weight, floor, 1.0);
    }

    /// <summary>
    /// The distinct local hours-of-day an event in [<paramref name="startUtc"/>, <paramref name="endUtc"/>)
    /// touches. A short event yields one hour; a multi-hour event yields every hour it spans, so its
    /// weight averages across them. Bounded so a pathological span can't loop unbounded.
    /// </summary>
    public static IReadOnlyCollection<int> LocalHoursSpanned(DateTime startUtc, DateTime endUtc, TimeZoneInfo tz)
    {
        var localStart = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc), tz);
        var localEnd = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(endUtc, DateTimeKind.Utc), tz);

        var hours = new HashSet<int>();
        var cursor = new DateTime(localStart.Year, localStart.Month, localStart.Day, localStart.Hour, 0, 0);
        for (var i = 0; i < 48 && cursor <= localEnd; i++, cursor = cursor.AddHours(1))
            hours.Add(cursor.Hour);
        if (hours.Count == 0) hours.Add(localStart.Hour);
        return hours;
    }
}
