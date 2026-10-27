using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Alerts.Delivery;

/// <summary>
/// Formats UTC timestamps on the notification clock (<see cref="Interfaces.INotificationTimeZoneResolver"/>)
/// for human-readable output. The zone's abbreviation is always shown, since the reader may be anywhere.
/// </summary>
internal static class TimestampFormatter
{
    /// <summary>
    /// Full format: "2026-02-24 14:42:50 CST"
    /// </summary>
    internal static string FormatLocal(DateTime utcTime, TimeZoneInfo zone)
    {
        var local = DateTimeUtilities.ToZone(utcTime, zone);
        return $"{local:yyyy-MM-dd HH:mm:ss} {DateTimeUtilities.ZoneAbbreviation(zone, utcTime)}";
    }

    /// <summary>
    /// Short format: "14:42 CST"
    /// </summary>
    internal static string FormatLocalShort(DateTime utcTime, TimeZoneInfo zone)
    {
        var local = DateTimeUtilities.ToZone(utcTime, zone);
        return $"{local:HH:mm} {DateTimeUtilities.ZoneAbbreviation(zone, utcTime)}";
    }
}
