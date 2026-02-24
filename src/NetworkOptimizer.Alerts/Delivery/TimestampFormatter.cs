namespace NetworkOptimizer.Alerts.Delivery;

/// <summary>
/// Formats UTC timestamps in the server's local timezone for human-readable output.
/// </summary>
internal static class TimestampFormatter
{
    /// <summary>
    /// Full format: "2026-02-24 14:42:50 CST"
    /// </summary>
    internal static string FormatLocal(DateTime utcTime)
    {
        var local = ToLocal(utcTime);
        return $"{local:yyyy-MM-dd HH:mm:ss} {GetTimezoneAbbreviation(local)}";
    }

    /// <summary>
    /// Short format: "14:42 CST"
    /// </summary>
    internal static string FormatLocalShort(DateTime utcTime)
    {
        var local = ToLocal(utcTime);
        return $"{local:HH:mm} {GetTimezoneAbbreviation(local)}";
    }

    private static DateTime ToLocal(DateTime utcTime)
    {
        // EF Core/SQLite returns Kind=Unspecified - treat as UTC
        var utc = utcTime.Kind == DateTimeKind.Local
            ? utcTime
            : DateTime.SpecifyKind(utcTime, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(
            utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime(),
            TimeZoneInfo.Local);
    }

    private static string GetTimezoneAbbreviation(DateTime localTime)
    {
        var tz = TimeZoneInfo.Local;
        var name = tz.IsDaylightSavingTime(localTime) ? tz.DaylightName : tz.StandardName;
        // On Linux, names are already short (CST, CDT). On Windows they're long.
        if (name.Length <= 5) return name;
        // Extract initials: "Central Standard Time" -> "CST"
        return string.Concat(name.Split(' ').Where(w => w.Length > 0).Select(w => w[0]));
    }
}
