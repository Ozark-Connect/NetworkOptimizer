namespace NetworkOptimizer.Reports;

/// <summary>
/// Shared display formatting utilities used by both web UI and PDF reports.
/// Centralizes formatting logic to ensure consistency between Audit.razor and PDF generation.
/// </summary>
public static class DisplayFormatters
{
    #region Device Name Formatting

    /// <summary>
    /// Strip any existing device type prefix from a name.
    /// Handles prefixes like [Gateway], [Switch], [AP], etc.
    /// Example: "[Switch] Office" → "Office"
    /// </summary>
    public static string StripDevicePrefix(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return deviceName ?? string.Empty;

        var name = deviceName.Trim();

        // Strip bracketed prefix like [Gateway], [Switch], [AP], etc.
        if (name.StartsWith("["))
        {
            var closeBracket = name.IndexOf(']');
            if (closeBracket > 0 && closeBracket < name.Length - 1)
            {
                name = name[(closeBracket + 1)..].TrimStart();
            }
        }

        return name;
    }

    /// <summary>
    /// Extract a clean network name from a device name for report titles.
    /// Strips prefixes like [Gateway], [Switch] and parenthetical suffixes like (UCG-Fiber).
    /// Example: "[Gateway] SeaTurtle Home" → "SeaTurtle Home"
    /// </summary>
    public static string ExtractNetworkName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return "Network";

        var name = StripDevicePrefix(deviceName);

        // Strip parenthetical suffix like (UCG-Fiber), (UDM Pro), etc.
        var openParen = name.LastIndexOf('(');
        if (openParen > 0 && name.EndsWith(")"))
        {
            name = name[..openParen].TrimEnd();
        }

        return string.IsNullOrWhiteSpace(name) ? "Network" : name;
    }

    /// <summary>
    /// Format a device name with consistent prefix.
    /// Strips any existing prefix and adds the correct one.
    /// Example: "Office Switch" with isGateway=false → "[Switch] Office Switch"
    /// </summary>
    public static string FormatDeviceName(string? deviceName, bool isGateway)
    {
        var cleanName = StripDevicePrefix(deviceName);
        var prefix = isGateway ? "[Gateway]" : "[Switch]";
        return $"{prefix} {cleanName}";
    }

    #endregion

    #region Network/VLAN Display

    /// <summary>
    /// Format network name with VLAN ID.
    /// Example: ("Main Network", 10) → "Main Network (10)"
    /// </summary>
    public static string FormatNetworkWithVlan(string? networkName, int? vlanId)
    {
        if (vlanId.HasValue)
            return $"{networkName ?? "Unknown"} ({vlanId})";
        return networkName ?? "Unknown";
    }

    /// <summary>
    /// Format VLAN display with native indicator.
    /// Example: (1) → "1 (native)", (10) → "10"
    /// </summary>
    public static string FormatVlanDisplay(int vlanId)
    {
        return vlanId == 1 ? $"{vlanId} (native)" : vlanId.ToString();
    }

    #endregion

    #region Port Status Display

    /// <summary>
    /// Get link status display string for a port.
    /// </summary>
    public static string GetLinkStatus(bool isUp, int speed)
    {
        if (!isUp) return "Down";
        if (speed >= 1000)
        {
            var gbe = speed / 1000.0;
            return gbe % 1 == 0 ? $"Up {(int)gbe} GbE" : $"Up {gbe:0.#} GbE";
        }
        if (speed > 0) return $"Up {speed} MbE";
        return "Down";
    }

    /// <summary>
    /// Get PoE status display string for a port.
    /// </summary>
    public static string GetPoeStatus(double poePower, string? poeMode, bool poeEnabled)
    {
        if (poePower > 0) return $"{poePower:F1} W";
        if (poeMode == "off") return "off";
        if (poeEnabled) return "off";
        return "N/A";
    }

    /// <summary>
    /// Get port security status display string.
    /// </summary>
    public static string GetPortSecurityStatus(int macCount, bool portSecurityEnabled)
    {
        if (macCount > 1) return $"{macCount} MAC";
        if (macCount == 1) return "1 MAC";
        if (portSecurityEnabled) return "Yes";
        return "-";
    }

    /// <summary>
    /// Get isolation status display string.
    /// </summary>
    public static string GetIsolationStatus(bool isolation)
    {
        return isolation ? "Yes" : "-";
    }

    #endregion

    #region DNS Display

    /// <summary>
    /// Get WAN DNS display string with Correct/Incorrect prefixes.
    /// Used by both web UI and PDF reports.
    /// </summary>
    public static string GetWanDnsDisplay(
        List<string> wanDnsServers,
        List<string?> wanDnsPtrResults,
        List<string> matchedDnsServers,
        List<string> mismatchedDnsServers,
        List<string> interfacesWithMismatch,
        List<string> interfacesWithoutDns,
        string? wanDnsProvider,
        string? expectedDnsProvider,
        bool wanDnsMatchesDoH,
        bool wanDnsOrderCorrect)
    {
        var parts = new List<string>();
        var providerInfo = expectedDnsProvider ?? wanDnsProvider ?? "matches DoH";

        // Always show matched servers first (if any)
        if (matchedDnsServers.Any())
        {
            var servers = string.Join(", ", matchedDnsServers);
            // Add "Correct to:" prefix only for Wrong Order case
            if (wanDnsMatchesDoH && !wanDnsOrderCorrect)
                parts.Add($"Correct to: {servers} ({providerInfo})");
            else
                parts.Add($"{servers} ({providerInfo})");
        }

        // Show mismatched interfaces
        if (interfacesWithMismatch.Any() && mismatchedDnsServers.Any())
        {
            var mismatchedIps = string.Join(", ", mismatchedDnsServers);
            parts.Add($"Incorrect: {mismatchedIps} on {string.Join(", ", interfacesWithMismatch)}");
        }

        // Show interfaces with no DNS configured
        if (interfacesWithoutDns.Any())
        {
            parts.Add($"Incorrect: No DNS on {string.Join(", ", interfacesWithoutDns)}");
        }

        // If no parts yet but we have WAN DNS servers, show them
        if (!parts.Any() && wanDnsServers.Any())
        {
            var provider = wanDnsProvider ?? expectedDnsProvider ?? "matches DoH";

            // If wrong order, show the correct order with "Should be" prefix
            if (wanDnsMatchesDoH && !wanDnsOrderCorrect && wanDnsServers.Count >= 2)
            {
                var correctOrder = GetCorrectDnsOrder(wanDnsServers, wanDnsPtrResults);
                parts.Add($"Should be {correctOrder} ({provider})");
            }
            else
            {
                var servers = string.Join(", ", wanDnsServers);
                parts.Add($"{servers} ({provider})");
            }
        }

        if (!parts.Any())
            return "Not Configured";

        return string.Join(" | ", parts);
    }

    /// <summary>
    /// Get correct DNS order by sorting dns1 before dns2.
    /// </summary>
    public static string GetCorrectDnsOrder(List<string> servers, List<string?> ptrResults)
    {
        // Pair IPs with their PTR results and sort by dns1 first, dns2 second
        var paired = servers.Zip(ptrResults, (ip, ptr) => (Ip: ip, Ptr: ptr ?? "")).ToList();

        // Sort: dns1 should come before dns2
        var sorted = paired
            .OrderBy(p => p.Ptr.Contains("dns2", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .Select(p => p.Ip)
            .ToList();

        return string.Join(", ", sorted);
    }

    /// <summary>
    /// Get WAN DNS status text.
    /// </summary>
    public static string GetWanDnsStatus(List<string> wanDnsServers, bool wanDnsMatchesDoH, bool wanDnsOrderCorrect)
    {
        if (!wanDnsServers.Any()) return "Not Configured";
        if (wanDnsMatchesDoH && !wanDnsOrderCorrect) return "Wrong Order";
        if (wanDnsMatchesDoH) return "Matched";
        return "Mismatched";
    }

    /// <summary>
    /// Get device DNS display string.
    /// </summary>
    public static string GetDeviceDnsDisplay(
        int totalDevicesChecked,
        int devicesWithCorrectDns,
        int dhcpDeviceCount,
        bool deviceDnsPointsToGateway)
    {
        if (totalDevicesChecked == 0 && dhcpDeviceCount == 0)
            return "No infrastructure devices to check";

        var parts = new List<string>();

        if (totalDevicesChecked > 0)
        {
            if (deviceDnsPointsToGateway)
                parts.Add($"{totalDevicesChecked} static IP device(s) point to gateway");
            else
            {
                var misconfigured = totalDevicesChecked - devicesWithCorrectDns;
                parts.Add($"{misconfigured} of {totalDevicesChecked} have non-gateway DNS");
            }
        }

        if (dhcpDeviceCount > 0)
            parts.Add($"{dhcpDeviceCount} use DHCP");

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Get device DNS status text.
    /// </summary>
    public static string GetDeviceDnsStatus(int totalDevicesChecked, int dhcpDeviceCount, bool deviceDnsPointsToGateway)
    {
        if (totalDevicesChecked == 0 && dhcpDeviceCount == 0) return "No Devices";
        if (deviceDnsPointsToGateway) return "Correct";
        return "Misconfigured";
    }

    /// <summary>
    /// Get DoH status display string.
    /// </summary>
    public static string GetDohStatusDisplay(
        bool dohEnabled,
        string dohState,
        List<string> dohProviders,
        List<string>? dohConfigNames = null)
    {
        if (!dohEnabled) return "Not Configured";

        // Show provider names with config names (e.g., "NextDNS (NextDNS-fcdba9)")
        if (dohProviders.Any())
        {
            var providers = string.Join(", ", dohProviders);
            var configNames = dohConfigNames?.Any() == true ? string.Join(", ", dohConfigNames) : null;

            // Only show config name if it differs from provider name
            var display = configNames != null && configNames != providers
                ? $"{providers} ({configNames})"
                : providers;

            if (dohState == "auto") return $"{display} (auto mode)";
            return display;
        }

        if (dohState == "auto") return "Auto (may fallback)";
        return "Enabled";
    }

    /// <summary>
    /// Get protection status display string.
    /// </summary>
    public static string GetProtectionStatusDisplay(
        bool fullyProtected,
        bool dnsLeakProtection,
        bool dotBlocked,
        bool dohBypassBlocked,
        bool wanDnsMatchesDoH,
        bool dohEnabled)
    {
        if (fullyProtected) return "Full Protection";

        var protections = new List<string>();
        if (dnsLeakProtection) protections.Add("DNS53");
        if (dotBlocked) protections.Add("DoT");
        if (dohBypassBlocked) protections.Add("DoH Bypass");
        if (wanDnsMatchesDoH) protections.Add("WAN DNS");

        if (protections.Any())
            return string.Join(" + ", protections);

        // No leak prevention but DoH is enabled
        if (dohEnabled)
            return "DoH Only - No Leak Prevention";

        return "Not Protected";
    }

    #endregion
}
