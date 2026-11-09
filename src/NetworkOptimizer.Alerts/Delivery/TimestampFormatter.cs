using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Alerts.Delivery;

/// <summary>
/// Formats UTC timestamps on the notification clock and in the notification format
/// (<see cref="Interfaces.INotificationTimeZoneResolver"/>) for human-readable output. The zone's
/// abbreviation is always shown, since the reader may be anywhere.
/// </summary>
internal static class TimestampFormatter
{
    /// <summary>
    /// Full format: "Feb 24, 2026, 14:42:50 CST", or "24 Feb 2026, 2:42:50 PM CST" for a 12-hour,
    /// day-first install.
    /// </summary>
    internal static string FormatLocal(DateTime utcTime, TimeZoneInfo zone, DisplayFormat format)
    {
        var local = DateTimeUtilities.ToZone(utcTime, zone);
        return $"{local.ToString(format.DateTimeSeconds, DisplayFormat.Culture)} {DateTimeUtilities.ZoneAbbreviation(zone, utcTime)}";
    }

    /// <summary>
    /// Short format: "14:42 CST", or "2:42 PM CST".
    /// </summary>
    internal static string FormatLocalShort(DateTime utcTime, TimeZoneInfo zone, DisplayFormat format)
    {
        var local = DateTimeUtilities.ToZone(utcTime, zone);
        return $"{local.ToString(format.Time, DisplayFormat.Culture)} {DateTimeUtilities.ZoneAbbreviation(zone, utcTime)}";
    }
}
