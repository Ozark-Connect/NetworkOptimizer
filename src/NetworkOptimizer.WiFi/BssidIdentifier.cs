namespace NetworkOptimizer.WiFi;

/// <summary>
/// Identifies hidden or unknown SSIDs based on BSSID patterns.
/// </summary>
public static class BssidIdentifier
{
    /// <summary>
    /// Known BSSID prefixes mapped to likely device/network types.
    /// Key is uppercase BSSID prefix (e.g., "62:45"), value is the friendly name.
    /// </summary>
    private static readonly Dictionary<string, string> KnownPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Xbox Wi-Fi Direct uses locally administered addresses starting with 62:45
        ["62:45"] = "Xbox Wi-Fi Direct",
    };

    /// <summary>
    /// Try to identify a hidden SSID based on its BSSID.
    /// </summary>
    /// <param name="bssid">The BSSID (MAC address) of the network</param>
    /// <returns>A friendly name if identified, null otherwise</returns>
    public static string? IdentifyByBssid(string? bssid)
    {
        if (string.IsNullOrEmpty(bssid))
            return null;

        // Normalize: uppercase, ensure colon separators
        var normalized = NormalizeBssid(bssid);
        if (normalized == null)
            return null;

        // Check prefixes from longest to shortest for most specific match
        foreach (var (prefix, name) in KnownPrefixes.OrderByDescending(kvp => kvp.Key.Length))
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return name;
        }

        return null;
    }

    /// <summary>
    /// Get a display name for a network, using BSSID identification for hidden SSIDs.
    /// </summary>
    /// <param name="ssid">The SSID (may be null/empty for hidden networks)</param>
    /// <param name="bssid">The BSSID</param>
    /// <returns>SSID if available, identified name if hidden and known, or "(Hidden)" otherwise</returns>
    public static string GetDisplayName(string? ssid, string? bssid)
    {
        if (!string.IsNullOrEmpty(ssid))
            return ssid;

        var identified = IdentifyByBssid(bssid);
        if (identified != null)
            return $"(Hidden: {identified})";

        return "(Hidden)";
    }

    /// <summary>
    /// Normalize a BSSID to uppercase with colon separators.
    /// </summary>
    private static string? NormalizeBssid(string bssid)
    {
        // Remove common separators and whitespace
        var clean = bssid.Replace("-", "").Replace(":", "").Replace(".", "").Trim();

        if (clean.Length != 12)
            return null;

        // Rebuild with colons
        return string.Join(":",
            Enumerable.Range(0, 6).Select(i => clean.Substring(i * 2, 2).ToUpperInvariant()));
    }
}
