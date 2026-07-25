namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Helper for formatting relative time strings with proper pluralization.
/// </summary>
public static class TimeFormatHelper
{
    /// <summary>
    /// Append the unit to a count, pluralized (e.g., "1 day", "2 days").
    /// </summary>
    public static string Pluralize(int value, string unit) =>
        $"{value} {unit}{(value == 1 ? "" : "s")}";

    /// <summary>
    /// Format a duration in long form with the two largest meaningful units
    /// (e.g., "1 day, 2 hours", "5 hours", "3 minutes").
    /// </summary>
    public static string FormatDuration(TimeSpan span)
    {
        if (span.TotalDays >= 1)
        {
            var dayPart = Pluralize((int)span.TotalDays, "day");
            return span.Hours > 0 ? $"{dayPart}, {Pluralize(span.Hours, "hour")}" : dayPart;
        }

        if (span.TotalHours >= 1)
            return Pluralize((int)span.TotalHours, "hour");

        return Pluralize((int)span.TotalMinutes, "minute");
    }

    /// <summary>
    /// Format a duration in compact form (e.g., "3d 4h", "5h 12m", "9m").
    /// </summary>
    public static string FormatDurationCompact(TimeSpan span)
    {
        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays}d {span.Hours}h";

        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}h {span.Minutes}m";

        return $"{(int)span.TotalMinutes}m";
    }

    /// <summary>
    /// Format a UTC time as a relative string (e.g., "5 minutes ago", "1 hour ago").
    /// </summary>
    public static string FormatRelativeTime(DateTime utcTime)
    {
        var elapsed = DateTime.UtcNow - utcTime;

        if (elapsed.TotalMinutes < 1)
            return "Just now";

        if (elapsed.TotalMinutes < 60)
            return $"{Pluralize((int)elapsed.TotalMinutes, "minute")} ago";

        if (elapsed.TotalHours < 24)
            return $"{Pluralize((int)elapsed.TotalHours, "hour")} ago";

        if (elapsed.TotalDays < 7)
            return $"{Pluralize((int)elapsed.TotalDays, "day")} ago";

        return utcTime.ToLocalTime().ToString("MMM dd, yyyy");
    }

    /// <summary>
    /// Format a UTC time as a short relative string (e.g., "5 mins ago", "1 hour ago").
    /// </summary>
    public static string FormatRelativeTimeShort(DateTime utcTime)
    {
        var elapsed = DateTime.UtcNow - utcTime;

        if (elapsed.TotalMinutes < 1)
            return "Just now";

        if (elapsed.TotalMinutes < 60)
        {
            var mins = (int)elapsed.TotalMinutes;
            return $"{mins} min ago";
        }

        if (elapsed.TotalHours < 24)
            return $"{Pluralize((int)elapsed.TotalHours, "hr")} ago";

        return $"{Pluralize((int)elapsed.TotalDays, "day")} ago";
    }

    /// <summary>
    /// Format a UTC time as a compact relative string (e.g., "5s ago", "3m ago", "2h ago").
    /// </summary>
    public static string FormatRelativeTimeCompact(DateTime utcTime)
    {
        var elapsed = DateTime.UtcNow - utcTime;

        if (elapsed.TotalSeconds < 60)
            return $"{(int)elapsed.TotalSeconds} s ago";

        if (elapsed.TotalMinutes < 60)
            return $"{(int)elapsed.TotalMinutes} m ago";

        if (elapsed.TotalHours < 24)
            return $"{(int)elapsed.TotalHours} h ago";

        return $"{(int)elapsed.TotalDays} d ago";
    }
}
