namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Helper for formatting relative time strings with proper pluralization.
/// </summary>
public static class TimeFormatHelper
{
    /// <summary>
    /// Format a UTC time as a relative string (e.g., "5 minutes ago", "1 hour ago").
    /// </summary>
    public static string FormatRelativeTime(DateTime utcTime)
    {
        var elapsed = DateTime.UtcNow - utcTime;

        if (elapsed.TotalMinutes < 1)
            return "Just now";

        if (elapsed.TotalMinutes < 60)
        {
            var mins = (int)elapsed.TotalMinutes;
            return $"{mins} {(mins == 1 ? "minute" : "minutes")} ago";
        }

        if (elapsed.TotalHours < 24)
        {
            var hours = (int)elapsed.TotalHours;
            return $"{hours} {(hours == 1 ? "hour" : "hours")} ago";
        }

        if (elapsed.TotalDays < 7)
        {
            var days = (int)elapsed.TotalDays;
            return $"{days} {(days == 1 ? "day" : "days")} ago";
        }

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
        {
            var hours = (int)elapsed.TotalHours;
            return $"{hours} {(hours == 1 ? "hr" : "hrs")} ago";
        }

        var days = (int)elapsed.TotalDays;
        return $"{days} {(days == 1 ? "day" : "days")} ago";
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
