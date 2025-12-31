namespace NetworkOptimizer.Reports;

/// <summary>
/// Complete data model for network audit reports
/// </summary>
public class ReportData
{
    public string ClientName { get; set; } = "Client";
    public DateTime GeneratedAt { get; set; } = DateTime.Now;
    public SecurityScore SecurityScore { get; set; } = new();
    public List<NetworkInfo> Networks { get; set; } = new();
    public List<DeviceInfo> Devices { get; set; } = new();
    public List<SwitchDetail> Switches { get; set; } = new();
    public List<AccessPointDetail> AccessPoints { get; set; } = new();
    public List<AuditIssue> CriticalIssues { get; set; } = new();
    public List<AuditIssue> RecommendedImprovements { get; set; } = new();
    public List<string> HardeningNotes { get; set; } = new();
    public List<string> TopologyNotes { get; set; } = new();
    public DnsSecuritySummary? DnsSecurity { get; set; }
}

/// <summary>
/// DNS security configuration summary
/// </summary>
public class DnsSecuritySummary
{
    public bool DohEnabled { get; set; }
    public string DohState { get; set; } = "disabled";
    public List<string> DohProviders { get; set; } = new();
    public bool DnsLeakProtection { get; set; }
    public bool DotBlocked { get; set; }
    public bool DohBypassBlocked { get; set; }
    public bool FullyProtected { get; set; }

    // WAN DNS validation
    public List<string> WanDnsServers { get; set; } = new();
    public List<string?> WanDnsPtrResults { get; set; } = new();
    public bool WanDnsMatchesDoH { get; set; }
    public bool WanDnsOrderCorrect { get; set; } = true;
    public string? WanDnsProvider { get; set; }
    public string? ExpectedDnsProvider { get; set; }

    public string GetDohStatusDisplay()
    {
        if (!DohEnabled) return "Not Configured";
        if (DohState == "auto") return "Auto (may fallback)";
        if (DohProviders.Any()) return string.Join(", ", DohProviders);
        return "Enabled";
    }

    public string GetProtectionStatusDisplay()
    {
        if (FullyProtected) return "Full Protection";
        var protections = new List<string>();
        if (DnsLeakProtection) protections.Add("DNS53");
        if (DotBlocked) protections.Add("DoT");
        if (DohBypassBlocked) protections.Add("DoH Bypass");
        if (WanDnsMatchesDoH) protections.Add("WAN DNS");

        if (protections.Any())
            return string.Join(" + ", protections);

        // No leak prevention but DoH is enabled
        if (DohEnabled)
            return "DoH Only - No Leak Prevention";

        return "Not Protected";
    }

    public string GetWanDnsDisplay()
    {
        if (!WanDnsServers.Any()) return "Not Configured";

        var provider = WanDnsProvider ?? ExpectedDnsProvider ?? "matches DoH";

        // If wrong order, show the correct order with "Should be" prefix
        if (WanDnsMatchesDoH && !WanDnsOrderCorrect && WanDnsServers.Count >= 2)
        {
            var correctOrder = GetCorrectDnsOrder();
            return $"Should be {correctOrder} ({provider})";
        }

        var servers = string.Join(", ", WanDnsServers);

        if (WanDnsMatchesDoH)
        {
            return $"{servers} ({provider})";
        }

        if (!string.IsNullOrEmpty(ExpectedDnsProvider))
            return $"{servers} - Expected {ExpectedDnsProvider}";

        return servers;
    }

    private string GetCorrectDnsOrder()
    {
        // Pair IPs with their PTR results and sort by dns1 first, dns2 second
        var paired = WanDnsServers.Zip(WanDnsPtrResults, (ip, ptr) => (Ip: ip, Ptr: ptr ?? "")).ToList();

        // Sort: dns1 should come before dns2
        var sorted = paired
            .OrderBy(p => p.Ptr.Contains("dns2", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .Select(p => p.Ip)
            .ToList();

        return string.Join(", ", sorted);
    }

    // Device DNS validation
    public bool DeviceDnsPointsToGateway { get; set; } = true;
    public int TotalDevicesChecked { get; set; }
    public int DevicesWithCorrectDns { get; set; }
    public int DhcpDeviceCount { get; set; }

    public string GetDeviceDnsDisplay()
    {
        if (TotalDevicesChecked == 0 && DhcpDeviceCount == 0)
            return "No infrastructure devices to check";

        var parts = new List<string>();

        if (TotalDevicesChecked > 0)
        {
            if (DeviceDnsPointsToGateway)
                parts.Add($"{TotalDevicesChecked} static device(s) point to gateway");
            else
            {
                var misconfigured = TotalDevicesChecked - DevicesWithCorrectDns;
                parts.Add($"{misconfigured} of {TotalDevicesChecked} have non-gateway DNS");
            }
        }

        if (DhcpDeviceCount > 0)
            parts.Add($"{DhcpDeviceCount} use DHCP");

        return string.Join(", ", parts);
    }
}

/// <summary>
/// Overall security posture rating
/// </summary>
public class SecurityScore
{
    public SecurityRating Rating { get; set; } = SecurityRating.Good;
    public int TotalDevices { get; set; }
    public int TotalPorts { get; set; }
    public int DisabledPorts { get; set; }
    public int MacRestrictedPorts { get; set; }
    public int UnprotectedActivePorts { get; set; }
    public int CriticalIssueCount { get; set; }
    public int WarningCount { get; set; }

    /// <summary>
    /// Calculate overall security rating based on issues
    /// </summary>
    public static SecurityRating CalculateRating(int criticalCount, int warningCount)
    {
        if (criticalCount == 0 && warningCount == 0)
            return SecurityRating.Excellent;
        if (criticalCount == 0)
            return SecurityRating.Good;
        if (criticalCount <= 2)
            return SecurityRating.Fair;
        return SecurityRating.NeedsWork;
    }
}

public enum SecurityRating
{
    Excellent,
    Good,
    Fair,
    NeedsWork
}

/// <summary>
/// Network/VLAN information
/// </summary>
public class NetworkInfo
{
    public string NetworkId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int VlanId { get; set; }
    public string Subnet { get; set; } = string.Empty;
    public string Purpose { get; set; } = "corporate";
    public NetworkType Type { get; set; } = NetworkType.Corporate;

    public string GetDisplayName() => VlanId == 1
        ? $"{Name} ({VlanId} - native)"
        : $"{Name} ({VlanId})";
}

public enum NetworkType
{
    Corporate,
    IoT,
    Security,
    Management,
    Guest,
    Other
}

/// <summary>
/// Device information (switches, gateways, APs, etc.)
/// </summary>
public class DeviceInfo
{
    public string Name { get; set; } = string.Empty;
    public string Mac { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string DeviceType { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string Firmware { get; set; } = string.Empty;
    public bool IsOnline { get; set; }
    public DateTime? LastSeen { get; set; }
}

/// <summary>
/// Access point with connected wireless clients
/// </summary>
public class AccessPointDetail
{
    public string Name { get; set; } = string.Empty;
    public string Mac { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public List<WirelessClientDetail> Clients { get; set; } = new();

    public int TotalClients => Clients.Count;
    public int IoTClients => Clients.Count(c => c.IsIoT);
    public int CameraClients => Clients.Count(c => c.IsCamera);
}

/// <summary>
/// Wireless client connected to an access point
/// </summary>
public class WirelessClientDetail
{
    public string DisplayName { get; set; } = string.Empty;
    public string Mac { get; set; } = string.Empty;
    public string? Network { get; set; }
    public int? VlanId { get; set; }
    public string DeviceCategory { get; set; } = string.Empty;
    public string? VendorName { get; set; }
    public int DetectionConfidence { get; set; }
    public bool IsIoT { get; set; }
    public bool IsCamera { get; set; }
    public bool HasIssue { get; set; }
    public string? IssueMessage { get; set; }
}

/// <summary>
/// Switch device with port details
/// </summary>
public class SwitchDetail
{
    public string Name { get; set; } = string.Empty;
    public string Mac { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string DeviceType { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public bool IsGateway { get; set; }
    public int MaxCustomMacAcls { get; set; }
    public List<PortDetail> Ports { get; set; } = new();

    public int TotalPorts => Ports.Count;
    public int DisabledPorts => Ports.Count(p => p.Forward == "disabled");
    public int MacRestrictedPorts => Ports.Count(p => p.MacRestrictionCount > 0);
    public int UnprotectedActivePorts => Ports.Count(p =>
        p.Forward == "native" && p.IsUp && p.MacRestrictionCount == 0 && !p.IsUplink);
}

/// <summary>
/// Individual port configuration and status
/// </summary>
public class PortDetail
{
    public int PortIndex { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsUp { get; set; }
    public int Speed { get; set; }
    public string Forward { get; set; } = "all";
    public bool IsUplink { get; set; }
    public string? NativeNetwork { get; set; }
    public int? NativeVlan { get; set; }
    public List<string> ExcludedNetworks { get; set; } = new();

    // PoE
    public bool PoeEnabled { get; set; }
    public double PoePower { get; set; }
    public string PoeMode { get; set; } = string.Empty;

    // Security
    public bool PortSecurityEnabled { get; set; }
    public List<string> PortSecurityMacs { get; set; } = new();
    public bool Isolation { get; set; }

    public int MacRestrictionCount => PortSecurityMacs.Count;

    public string GetLinkStatus()
    {
        if (!IsUp) return "Down";
        if (Speed >= 1000)
        {
            var gbe = Speed / 1000.0;
            // Show decimal only if needed (e.g., 2.5 GbE, but 1 GbE not 1.0 GbE)
            return gbe % 1 == 0 ? $"Up {(int)gbe} GbE" : $"Up {gbe:0.#} GbE";
        }
        if (Speed > 0) return $"Up {Speed} MbE";
        return "Down";
    }

    public string GetPoeStatus()
    {
        if (PoePower > 0) return $"{PoePower:F1} W";
        if (PoeMode == "off") return "off";
        if (PoeEnabled) return "off";
        return "N/A";
    }

    public string GetPortSecurityStatus()
    {
        if (MacRestrictionCount > 1) return $"{MacRestrictionCount} MAC";
        if (MacRestrictionCount == 1) return "1 MAC";
        if (PortSecurityEnabled) return "Yes";
        return "-";
    }

    public string GetIsolationStatus() => Isolation ? "Yes" : "-";

    public (string Status, PortStatusType StatusType) GetStatus(bool supportsAcls = true)
    {
        // Check for possible IoT device on wrong VLAN (warning, not critical - needs user verification)
        if (IsIoTDeviceOnWrongVlan())
            return ("Possible Wrong VLAN", PortStatusType.Warning);

        if (Forward == "disabled")
            return ("Disabled", PortStatusType.Ok);

        if (!IsUp && Forward != "disabled")
            return ("Off", PortStatusType.Ok);

        if (IsUplink || Name.ToLower().Contains("uplink"))
            return ("Trunk", PortStatusType.Ok);

        if (Forward == "all")
            return ("Trunk", PortStatusType.Ok);

        if (Forward == "custom" || Forward == "customize")
        {
            if (Name.ToLower().Contains("ap") || Name.ToLower().Contains("access point"))
                return ("AP", PortStatusType.Ok);
            return ("OK", PortStatusType.Ok);
        }

        if (Forward == "native")
        {
            // Warning if no MAC restriction and device supports it
            if (IsUp && supportsAcls && MacRestrictionCount == 0 && !IsUplink)
                return ("No MAC", PortStatusType.Warning);
            return ("OK", PortStatusType.Ok);
        }

        return ("OK", PortStatusType.Ok);
    }

    private bool IsIoTDeviceOnWrongVlan()
    {
        var iotHints = new[] { "ikea", "hue", "smart", "iot", "alexa", "echo", "nest", "ring" };
        var nameLower = Name.ToLower();
        var isIoTDevice = iotHints.Any(hint => nameLower.Contains(hint));
        var onIoTVlan = NativeNetwork?.ToLower().Contains("iot") ?? false;

        return isIoTDevice && !onIoTVlan && Forward == "native" && IsUp;
    }
}

public enum PortStatusType
{
    Ok,
    Warning,
    Critical
}

/// <summary>
/// Security audit issue or recommendation
/// </summary>
public class AuditIssue
{
    public IssueType Type { get; set; }
    public IssueSeverity Severity { get; set; }
    public string SwitchName { get; set; } = string.Empty;
    public int? PortIndex { get; set; }
    public string PortName { get; set; } = string.Empty;
    public string CurrentNetwork { get; set; } = string.Empty;
    public int? CurrentVlan { get; set; }
    public string RecommendedAction { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    // Wireless-specific fields
    public bool IsWireless { get; set; }
    public string? ClientName { get; set; }
    public string? ClientMac { get; set; }
    public string? AccessPoint { get; set; }

    /// <summary>
    /// Get display text for Device column (the actual device/client name)
    /// </summary>
    public string GetDeviceDisplay()
    {
        if (IsWireless)
        {
            // Use the client name directly
            return ClientName ?? ClientMac ?? "Unknown Client";
        }

        // For wired, extract client name from "ClientName on SwitchName" format
        if (SwitchName.Contains(" on "))
        {
            return SwitchName.Split(" on ")[0];
        }

        // Fallback to port name or switch name
        return !string.IsNullOrEmpty(PortName) ? PortName : SwitchName;
    }

    /// <summary>
    /// Get display text for Port/Location column (where the device is connected)
    /// </summary>
    public string GetPortDisplay()
    {
        if (IsWireless)
        {
            // Show AP name
            return $"on {AccessPoint ?? "Unknown AP"}";
        }

        // For wired, show port info and switch
        var portInfo = PortIndex.HasValue ? $"{PortIndex} ({PortName})" : PortName;

        // Extract switch name from "ClientName on SwitchName" format
        if (SwitchName.Contains(" on "))
        {
            var switchPart = SwitchName.Split(" on ")[1];
            return $"{portInfo}\non {switchPart}";
        }

        return portInfo;
    }
}

public enum IssueType
{
    IoTWrongVlan,
    NoMacRestriction,
    UnusedPortNotDisabled,
    WeakPoEConfiguration,
    MissingPortSecurity,
    NoIsolation,
    Other
}

public enum IssueSeverity
{
    Critical,
    Warning,
    Info
}

/// <summary>
/// Port security coverage summary per switch
/// </summary>
public class PortSecuritySummary
{
    public string SwitchName { get; set; } = string.Empty;
    public int TotalPorts { get; set; }
    public int DisabledPorts { get; set; }
    public int MacRestrictedPorts { get; set; }
    public int UnprotectedActivePorts { get; set; }
    public bool SupportsAcls { get; set; }

    public double ProtectionPercentage => TotalPorts > 0
        ? (double)(DisabledPorts + MacRestrictedPorts) / TotalPorts * 100
        : 0;
}
