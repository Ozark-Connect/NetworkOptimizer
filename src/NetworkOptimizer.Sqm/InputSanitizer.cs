using System.Net;
using System.Text.RegularExpressions;

namespace NetworkOptimizer.Sqm;

/// <summary>
/// Validates and sanitizes user inputs for SQM script generation to prevent command injection.
/// All user-provided strings that end up in shell scripts must pass through this class.
/// </summary>
public static partial class InputSanitizer
{
    /// <summary>
    /// Validates that a ping host is a valid IP address or hostname.
    /// Prevents command injection via the PING_HOST variable.
    /// </summary>
    public static (bool isValid, string? error) ValidatePingHost(string? pingHost)
    {
        if (string.IsNullOrWhiteSpace(pingHost))
        {
            return (false, "Ping Target Host is required");
        }

        // Check if it's a valid IP address
        if (IPAddress.TryParse(pingHost, out _))
        {
            return (true, null);
        }

        // Check if it's a valid hostname (RFC 1123)
        // Allows: letters, numbers, hyphens, dots
        // Max 253 chars total, each label max 63 chars
        if (pingHost.Length > 253)
        {
            return (false, "Ping Target Host too long (max 253 characters)");
        }

        if (!HostnameRegex().IsMatch(pingHost))
        {
            return (false, "Invalid ping target host. Use an IP address or hostname (letters, numbers, hyphens, dots only)");
        }

        // Check each label length
        var labels = pingHost.Split('.');
        foreach (var label in labels)
        {
            if (label.Length > 63)
            {
                return (false, "Ping Target Host segment too long (max 63 characters per segment)");
            }
            if (label.StartsWith('-') || label.EndsWith('-'))
            {
                return (false, "Ping Target Host segments cannot start or end with a hyphen");
            }
        }

        return (true, null);
    }

    /// <summary>
    /// Validates that a speedtest server ID is numeric or empty.
    /// Prevents command injection via --server-id argument.
    /// </summary>
    public static (bool isValid, string? error) ValidateSpeedtestServerId(string? serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId))
        {
            return (true, null); // Empty is valid (uses auto-select)
        }

        // Server IDs are numeric
        if (!NumericRegex().IsMatch(serverId))
        {
            return (false, "Speedtest server ID must be numeric");
        }

        if (serverId.Length > 10)
        {
            return (false, "Speedtest server ID too long");
        }

        return (true, null);
    }

    /// <summary>
    /// Sanitizes a connection name for safe use in filenames and shell variables.
    /// Returns a sanitized version of the name.
    /// </summary>
    public static string SanitizeConnectionName(string? connectionName)
    {
        if (string.IsNullOrWhiteSpace(connectionName))
        {
            return "wan";
        }

        // Convert to lowercase and replace unsafe characters
        var sanitized = connectionName.ToLowerInvariant();

        // Only allow alphanumeric and hyphens
        sanitized = SafeNameRegex().Replace(sanitized, "-");

        // Collapse multiple hyphens
        sanitized = MultipleHyphensRegex().Replace(sanitized, "-");

        // Trim hyphens from start/end
        sanitized = sanitized.Trim('-');

        // Ensure we have something left
        if (string.IsNullOrEmpty(sanitized))
        {
            return "wan";
        }

        // Limit length
        if (sanitized.Length > 32)
        {
            sanitized = sanitized[..32].TrimEnd('-');
        }

        return sanitized;
    }

    /// <summary>
    /// Validates a cron schedule entry.
    /// Format: minute hour [day-of-month month day-of-week]
    /// </summary>
    public static (bool isValid, string? error) ValidateCronSchedule(string? schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule))
        {
            return (false, "Schedule is required");
        }

        var parts = schedule.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts.Length > 5)
        {
            return (false, "Invalid cron format. Expected: minute hour [day month weekday]");
        }

        // Validate minute (0-59, or *)
        if (!IsValidCronField(parts[0], 0, 59))
        {
            return (false, "Invalid minute field (0-59, *, or */n)");
        }

        // Validate hour (0-23, or *)
        if (!IsValidCronField(parts[1], 0, 23))
        {
            return (false, "Invalid hour field (0-23, *, or */n)");
        }

        // If more fields provided, validate them too
        if (parts.Length >= 3 && !IsValidCronField(parts[2], 1, 31))
        {
            return (false, "Invalid day-of-month field (1-31, *, or */n)");
        }

        if (parts.Length >= 4 && !IsValidCronField(parts[3], 1, 12))
        {
            return (false, "Invalid month field (1-12, *, or */n)");
        }

        if (parts.Length >= 5 && !IsValidCronField(parts[4], 0, 7))
        {
            return (false, "Invalid day-of-week field (0-7, *, or */n)");
        }

        return (true, null);
    }

    /// <summary>
    /// Validates a single cron field value.
    /// </summary>
    private static bool IsValidCronField(string field, int min, int max)
    {
        // Allow *
        if (field == "*")
        {
            return true;
        }

        // Allow */n (step values)
        if (field.StartsWith("*/"))
        {
            var stepPart = field[2..];
            return int.TryParse(stepPart, out var step) && step >= 1 && step <= max;
        }

        // Allow ranges (n-m)
        if (field.Contains('-'))
        {
            var rangeParts = field.Split('-');
            if (rangeParts.Length != 2)
            {
                return false;
            }
            return int.TryParse(rangeParts[0], out var start) &&
                   int.TryParse(rangeParts[1], out var end) &&
                   start >= min && start <= max &&
                   end >= min && end <= max &&
                   start <= end;
        }

        // Allow comma-separated values
        if (field.Contains(','))
        {
            var values = field.Split(',');
            return values.All(v => int.TryParse(v, out var val) && val >= min && val <= max);
        }

        // Simple numeric value
        return int.TryParse(field, out var value) && value >= min && value <= max;
    }

    /// <summary>
    /// Validates an interface name for safe use in shell scripts.
    /// </summary>
    public static (bool isValid, string? error) ValidateInterface(string? interfaceName)
    {
        if (string.IsNullOrWhiteSpace(interfaceName))
        {
            return (false, "Interface is required");
        }

        // Interface names: alphanumeric and common chars like eth0, ppp0, br0
        if (!InterfaceRegex().IsMatch(interfaceName))
        {
            return (false, "Invalid interface name. Use alphanumeric characters only (e.g., eth0, ppp0)");
        }

        if (interfaceName.Length > 15) // Linux IFNAMSIZ is 16 including null
        {
            return (false, "Interface name too long (max 15 characters)");
        }

        return (true, null);
    }

    /// <summary>
    /// Escapes a string for safe use in a shell double-quoted string.
    /// Use this when a value must be embedded in double quotes.
    /// </summary>
    public static string EscapeForShellDoubleQuote(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // In double quotes, escape: $ ` \ " !
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("$", "\\$")
            .Replace("`", "\\`")
            .Replace("!", "\\!");
    }

    /// <summary>
    /// Trims and normalizes a ping host value. Returns null if empty.
    /// </summary>
    public static string? TrimPingHost(string? pingHost)
        => string.IsNullOrWhiteSpace(pingHost) ? null : pingHost.Trim();

    /// <summary>
    /// Trims and normalizes a speedtest server ID. Returns null if empty.
    /// </summary>
    public static string? TrimSpeedtestServerId(string? serverId)
        => string.IsNullOrWhiteSpace(serverId) ? null : serverId.Trim();

    /// <summary>
    /// Trims and normalizes an interface name. Returns null if empty.
    /// </summary>
    public static string? TrimInterface(string? interfaceName)
        => string.IsNullOrWhiteSpace(interfaceName) ? null : interfaceName.Trim();

    // Compiled regex patterns for performance
    [GeneratedRegex(@"^[a-zA-Z0-9]([a-zA-Z0-9\-\.]*[a-zA-Z0-9])?$")]
    private static partial Regex HostnameRegex();

    [GeneratedRegex(@"^[0-9]+$")]
    private static partial Regex NumericRegex();

    [GeneratedRegex(@"[^a-z0-9\-]")]
    private static partial Regex SafeNameRegex();

    [GeneratedRegex(@"-+")]
    private static partial Regex MultipleHyphensRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9_\-\.]+$")]
    private static partial Regex InterfaceRegex();
}
