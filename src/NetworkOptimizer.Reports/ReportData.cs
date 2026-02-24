using NetworkOptimizer.Core.Helpers;

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
    public List<OfflineClientDetail> OfflineClients { get; set; } = new();
    public List<AuditIssue> CriticalIssues { get; set; } = new();
    public List<AuditIssue> RecommendedImprovements { get; set; } = new();
    public List<string> HardeningNotes { get; set; } = new();
    public List<string> TopologyNotes { get; set; } = new();
    public DnsSecuritySummary? DnsSecurity { get; set; }
    public ThreatSummaryData? ThreatSummary { get; set; }
}

/// <summary>
/// DNS security configuration summary
/// </summary>
public class DnsSecuritySummary
{
    public bool DohEnabled { get; set; }
    public string DohState { get; set; } = "disabled";
    public List<string> DohProviders { get; set; } = new();
    public List<string> DohConfigNames { get; set; } = new();
    public bool DnsLeakProtection { get; set; }
    public bool HasDns53BlockRule { get; set; }
    public bool Dns53ProvidesFullCoverage { get; set; }
    public bool DnatProvidesFullCoverage { get; set; }
    public bool DotBlocked { get; set; }
    public bool DotProvidesFullCoverage { get; set; }
    public bool DoqBlocked { get; set; }
    public bool DoqProvidesFullCoverage { get; set; }
    public bool DohBypassBlocked { get; set; }
    public bool FullyProtected { get; set; }

    // WAN DNS validation
    public List<string> WanDnsServers { get; set; } = new();
    public List<string?> WanDnsPtrResults { get; set; } = new();
    public bool WanDnsMatchesDoH { get; set; }
    public bool WanDnsOrderCorrect { get; set; } = true;
    public string? WanDnsProvider { get; set; }
    public string? ExpectedDnsProvider { get; set; }
    public List<string> MismatchedDnsServers { get; set; } = new();
    public List<string> MatchedDnsServers { get; set; } = new();
    public List<string> InterfacesWithMismatch { get; set; } = new();
    public List<string> InterfacesWithoutDns { get; set; } = new();

    public string GetDohStatusDisplay()
    {
        return DisplayFormatters.GetDohStatusDisplay(DohEnabled, DohState, DohProviders, DohConfigNames);
    }

    public string GetProtectionStatusDisplay()
    {
        return DisplayFormatters.GetProtectionStatusDisplay(
            FullyProtected, DnsLeakProtection, DotBlocked, DohBypassBlocked, WanDnsMatchesDoH, DohEnabled);
    }

    public string GetWanDnsDisplay()
    {
        return DisplayFormatters.GetWanDnsDisplay(
            WanDnsServers, WanDnsPtrResults, MatchedDnsServers, MismatchedDnsServers,
            InterfacesWithMismatch, InterfacesWithoutDns,
            WanDnsProvider, ExpectedDnsProvider, WanDnsMatchesDoH, WanDnsOrderCorrect);
    }

    public string GetDnsLeakProtectionDetail()
    {
        if (!DnsLeakProtection)
        {
            if (HasDns53BlockRule)
                return "External DNS queries partially blocked";
            return "Devices can bypass network DNS";
        }

        if (DnatProvidesFullCoverage && HasDns53BlockRule && Dns53ProvidesFullCoverage)
            return "External DNS queries redirected and leakage blocked";
        if (DnatProvidesFullCoverage && HasDns53BlockRule)
            return "External DNS queries redirected and leakage partially blocked";
        if (DnatProvidesFullCoverage)
            return "External DNS queries redirected";
        return "External DNS queries blocked";
    }

    // Device DNS validation
    public bool DeviceDnsPointsToGateway { get; set; } = true;
    public int TotalDevicesChecked { get; set; }
    public int DevicesWithCorrectDns { get; set; }
    public int DhcpDeviceCount { get; set; }

    public string GetDeviceDnsDisplay()
    {
        return DisplayFormatters.GetDeviceDnsDisplay(
            TotalDevicesChecked, DevicesWithCorrectDns, DhcpDeviceCount, DeviceDnsPointsToGateway);
    }

    // Third-party DNS (Pi-hole, etc.)
    public bool HasThirdPartyDns { get; set; }
    public bool IsPiholeDetected { get; set; }
    public string? ThirdPartyDnsProviderName { get; set; }
    public List<ThirdPartyDnsNetworkInfo> ThirdPartyNetworks { get; set; } = new();
}

/// <summary>
/// Third-party DNS network information for reports
/// </summary>
public class ThirdPartyDnsNetworkInfo
{
    public required string NetworkName { get; init; }
    public int VlanId { get; init; }
    public required string DnsServerIp { get; init; }
    public string? DnsProviderName { get; init; }
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

    /// <summary>
    /// Convert purpose string to NetworkType enum
    /// </summary>
    public static NetworkType ParsePurpose(string? purpose) => purpose?.ToLowerInvariant() switch
    {
        "home" => NetworkType.Home,
        "iot" => NetworkType.IoT,
        "security" => NetworkType.Security,
        "management" => NetworkType.Management,
        "guest" => NetworkType.Guest,
        "corporate" => NetworkType.Corporate,
        _ => NetworkType.Other
    };
}

public enum NetworkType
{
    Corporate,
    Home,
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
    public string? IssueTitle { get; set; }
    public string? IssueMessage { get; set; }
}

/// <summary>
/// Offline client from history API
/// </summary>
public class OfflineClientDetail
{
    public string DisplayName { get; set; } = string.Empty;
    public string Mac { get; set; } = string.Empty;
    public string? Network { get; set; }
    public int? VlanId { get; set; }
    public string DeviceCategory { get; set; } = string.Empty;
    public string? LastUplinkName { get; set; }
    public string LastSeenDisplay { get; set; } = string.Empty;
    public bool IsRecentlyActive { get; set; }
    public bool IsIoT { get; set; }
    public bool IsCamera { get; set; }
    public bool HasIssue { get; set; }
    public string? IssueTitle { get; set; }
    public string? IssueSeverity { get; set; }
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
    public int Dot1xPorts => Ports.Count(p => p.Dot1xCtrl is ("auto" or "mac_based") && p.Forward == "native" && p.IsUp && !p.IsUplink);
    public int UnprotectedActivePorts => Ports.Count(p =>
        p.Forward == "native" && p.IsUp && p.MacRestrictionCount == 0 && !p.IsUplink
        && p.Dot1xCtrl is not ("auto" or "mac_based"));
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

    /// <summary>
    /// Type of UniFi device connected to this port (e.g., "uap", "usw"). Null for regular clients.
    /// </summary>
    public string? ConnectedDeviceType { get; set; }

    /// <summary>
    /// 802.1X control mode: "auto", "mac_based", "force_authorized", "force_unauthorized", or null.
    /// </summary>
    public string? Dot1xCtrl { get; set; }

    public int MacRestrictionCount => PortSecurityMacs.Count;

    public string GetLinkStatus() => DisplayFormatters.GetLinkStatus(IsUp, Speed);

    public string GetPoeStatus() => DisplayFormatters.GetPoeStatus(PoePower, PoeMode, PoeEnabled);

    public string GetPortSecurityStatus() => DisplayFormatters.GetPortSecurityStatus(MacRestrictionCount, PortSecurityEnabled, Dot1xCtrl);

    public string GetIsolationStatus() => DisplayFormatters.GetIsolationStatus(Isolation);

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

        // Check if this port has a UniFi device connected
        var deviceStatus = GetConnectedDeviceStatus();
        if (deviceStatus != null)
            return (deviceStatus, PortStatusType.Ok);

        if (Forward == "custom" || Forward == "customize")
            return ("OK", PortStatusType.Ok);

        if (Forward == "native")
        {
            // Warning if no MAC restriction, no 802.1X, and device supports it
            if (IsUp && supportsAcls && MacRestrictionCount == 0 && !IsUplink
                && Dot1xCtrl is not ("auto" or "mac_based"))
                return ("No MAC", PortStatusType.Warning);
            return ("OK", PortStatusType.Ok);
        }

        return ("OK", PortStatusType.Ok);
    }

    /// <summary>
    /// Get display status for ports with UniFi devices connected.
    /// Returns null if not a recognized device type.
    /// </summary>
    private string? GetConnectedDeviceStatus()
    {
        // Primary: check actual device type from uplink data
        if (!string.IsNullOrEmpty(ConnectedDeviceType))
        {
            return ConnectedDeviceType.ToLowerInvariant() switch
            {
                "uap" => "AP",
                "usw" => "Switch",
                "ubb" => "Bridge",
                "ugw" or "usg" or "udm" or "uxg" or "ucg" => "Gateway",
                "umbb" => "Modem",
                "uck" => "CloudKey",
                _ => "Device"  // Generic for unknown UniFi device types
            };
        }

        // Fallback: check port name for AP hints
        var nameLower = Name?.ToLower() ?? "";
        if (nameLower.Contains("ap") || nameLower.Contains("access point"))
            return "AP";

        return null;
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
    public string? SwitchMac { get; set; }  // MAC address for reliable switch identification
    public int? PortIndex { get; set; }
    public string? PortId { get; set; }  // Non-integer port identifier (e.g., "WAN1")
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
    public string? WifiBand { get; set; }

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

        // If we have a valid SwitchName (gateway/switch name), use it
        if (!string.IsNullOrEmpty(SwitchName) && SwitchName != "Unknown")
        {
            return SwitchName;
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
            // Show AP name with WiFi band if available
            var apName = AccessPoint ?? "Unknown AP";
            return !string.IsNullOrEmpty(WifiBand)
                ? $"{apName} ({WifiBand})"
                : apName;
        }

        // For non-integer port IDs (e.g., "WAN1"), show PortId with PortName
        if (!string.IsNullOrEmpty(PortId))
        {
            return !string.IsNullOrEmpty(PortName) ? $"{PortId} ({PortName})" : PortId;
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

/// <summary>
/// Threat intelligence summary for PDF/Markdown reports
/// </summary>
public class ThreatSummaryData
{
    public int TotalEvents { get; set; }
    public int TotalBlocked { get; set; }
    public int TotalDetected { get; set; }
    public int UniqueSourceIps { get; set; }
    public string TimeRange { get; set; } = "Last 30 days";
    public Dictionary<string, int> ByKillChain { get; set; } = new();
    public List<ThreatSourceEntry> TopSources { get; set; } = new();
    public List<ExposedServiceEntry> ExposedServices { get; set; } = new();
}

public class ThreatSourceEntry
{
    public string Ip { get; set; } = string.Empty;
    public string? CountryCode { get; set; }
    public string? AsnOrg { get; set; }
    public int EventCount { get; set; }
}

public class ExposedServiceEntry
{
    public int Port { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public string ForwardTarget { get; set; } = string.Empty;
    public int ThreatCount { get; set; }
    public int UniqueSourceIps { get; set; }
}
