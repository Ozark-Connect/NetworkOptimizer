namespace NetworkOptimizer.UniFi;

/// <summary>
/// Utility methods for formatting Wi-Fi radio band and protocol information
/// </summary>
public static class RadioFormatHelper
{
    /// <summary>
    /// Formats a UniFi radio band code to a human-readable frequency
    /// </summary>
    /// <param name="radio">UniFi radio code (ng, na, 6e)</param>
    /// <returns>Human-readable frequency (e.g., "5 GHz")</returns>
    public static string FormatBand(string? radio)
    {
        if (string.IsNullOrEmpty(radio))
            return "";

        return radio.ToLowerInvariant() switch
        {
            "ng" => "2.4 GHz",
            "na" => "5 GHz",
            "6e" => "6 GHz",
            _ => radio
        };
    }

    /// <summary>
    /// Formats a UniFi radio protocol code to a Wi-Fi generation string
    /// </summary>
    /// <param name="proto">Protocol code (a, b, g, n, ac, ax, be)</param>
    /// <param name="radio">Optional radio band to determine 6E vs 6</param>
    /// <returns>Wi-Fi generation string (e.g., "Wi-Fi 6 (ax)" or "Wi-Fi 6E (ax)")</returns>
    public static string FormatProtocol(string? proto, string? radio = null)
    {
        if (string.IsNullOrEmpty(proto))
            return "";

        var protoLower = proto.ToLowerInvariant();
        var is6GHz = radio?.ToLowerInvariant() == "6e";

        return protoLower switch
        {
            "a" => "Wi-Fi 1/2 (a)",
            "b" => "Wi-Fi 1 (b)",
            "g" => "Wi-Fi 3 (g)",
            "n" => "Wi-Fi 4 (n)",
            "ac" => "Wi-Fi 5 (ac)",
            "ax" => is6GHz ? "Wi-Fi 6E (ax)" : "Wi-Fi 6 (ax)",
            "be" => "Wi-Fi 7 (be)",
            _ => $"Wi-Fi ({proto})"
        };
    }

    /// <summary>
    /// Gets a simple protocol suffix (e.g., "6 (ax)") for compact display
    /// </summary>
    /// <param name="proto">Protocol code</param>
    /// <param name="radio">Optional radio band to determine 6E vs 6</param>
    /// <returns>Protocol suffix for "Wi-Fi X" format</returns>
    public static string FormatProtocolSuffix(string? proto, string? radio = null)
    {
        if (string.IsNullOrEmpty(proto))
            return "";

        var protoLower = proto.ToLowerInvariant();
        var is6GHz = radio?.ToLowerInvariant() == "6e";

        return protoLower switch
        {
            "a" => "1/2 (a)",
            "b" => "1 (b)",
            "g" => "3 (g)",
            "n" => "4 (n)",
            "ac" => "5 (ac)",
            "ax" => is6GHz ? "6E (ax)" : "6 (ax)",
            "be" => "7 (be)",
            _ => $"({proto})"
        };
    }

}
