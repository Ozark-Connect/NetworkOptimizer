namespace NetworkOptimizer.Core.Helpers;

/// <summary>Timestamp handling shared across the app.</summary>
public static class DateTimeUtilities
{
    /// <summary>
    /// A stored timestamp as a genuine UTC instant, safe to hand to InfluxDB.
    ///
    /// Every timestamp this app stores is UTC, but SQLite returns them Unspecified - and
    /// <c>ToUniversalTime</c> reads Unspecified as LOCAL and converts, so on a container running a
    /// non-UTC timezone a window silently shifts by the offset. Twice now that has meant querying
    /// hours that had not happened yet and getting an empty result rather than an error: once for a
    /// firmware rollout's post-upgrade window, once for a speed test's wireless rates.
    ///
    /// Unspecified is taken at face value; anything already Local is converted properly.
    /// </summary>
    public static DateTime AsUtc(DateTime t) =>
        t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime();

    /// <summary>A stored timestamp on <paramref name="zone"/>'s clock. Unspecified is read as UTC, as in <see cref="AsUtc"/>.</summary>
    public static DateTime ToZone(DateTime t, TimeZoneInfo zone) => TimeZoneInfo.ConvertTimeFromUtc(AsUtc(t), zone);

    /// <summary>
    /// Short name of a zone at an instant: "CDT" where the platform has one (Linux), else the
    /// initials of the long name ("Central Daylight Time" on Windows).
    /// </summary>
    public static string ZoneAbbreviation(TimeZoneInfo zone, DateTime instant)
    {
        if (zone.BaseUtcOffset == TimeSpan.Zero && !zone.SupportsDaylightSavingTime) return "UTC";
        var name = zone.IsDaylightSavingTime(ToZone(instant, zone)) ? zone.DaylightName : zone.StandardName;
        if (name.Length <= 5) return name;
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 1 ? string.Concat(words.Select(w => w[0])) : name;
    }
}
