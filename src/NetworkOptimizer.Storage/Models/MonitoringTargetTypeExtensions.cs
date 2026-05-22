using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Display formatters for monitoring enums. Acronyms like WAN, ISP, ICMP must render
/// uppercase even when the C# enum case is PascalCase.
/// </summary>
public static class MonitoringEnumDisplay
{
    public static string ToDisplayName(this MonitoringTargetType t) => t switch
    {
        MonitoringTargetType.Fabric => "Fabric",
        MonitoringTargetType.Wan => "WAN",
        MonitoringTargetType.AccessIsp => "Access ISP",
        MonitoringTargetType.Transit => "Transit",
        MonitoringTargetType.Custom => "Custom",
        MonitoringTargetType.InternetService => "Internet",
        _ => t.ToString()
    };

    public static string ToDisplayName(this ProbeMode m) => m switch
    {
        ProbeMode.Icmp => "ICMP",
        ProbeMode.Tcp => "TCP",
        ProbeMode.Udp => "UDP",
        _ => m.ToString().ToUpperInvariant()
    };

    public static string ToDisplayName(this AccessTechnology a) => a switch
    {
        AccessTechnology.Unknown => "Unknown",
        AccessTechnology.Gpon => "GPON",
        AccessTechnology.XgsPon => "XGS-PON",
        AccessTechnology.Docsis => "DOCSIS",
        AccessTechnology.PppoE => "PPPoE",
        AccessTechnology.DirectEthernet => "Direct Ethernet",
        AccessTechnology.FixedWireless => "Fixed Wireless",
        AccessTechnology.Satellite => "Satellite",
        AccessTechnology.Cellular => "Cellular",
        AccessTechnology.Other => "Other",
        _ => a.ToString()
    };

    public static string ToDisplayName(this UpstreamRole r) => r switch
    {
        UpstreamRole.Olt => "OLT",
        UpstreamRole.Cmts => "CMTS",
        UpstreamRole.Bng => "BNG",
        UpstreamRole.Aggregation => "Aggregation",
        UpstreamRole.Border => "Border",
        UpstreamRole.Transit => "Transit",
        UpstreamRole.PathProxy => "Path proxy",
        UpstreamRole.AccessHop => "Access hop",
        _ => r.ToString()
    };

    public static string ToDisplayName(this InterfaceDirection d) => d switch
    {
        InterfaceDirection.Unknown => "Unknown",
        InterfaceDirection.Uplink => "Uplink",
        InterfaceDirection.Downlink => "Downlink",
        InterfaceDirection.Wan => "WAN",
        _ => d.ToString()
    };

    public static string ToDisplayName(this DiscoveryMethod m) => m switch
    {
        DiscoveryMethod.DirectRouter => "Router probe",
        DiscoveryMethod.PathProxy => "Path-end",
        DiscoveryMethod.UserProvided => "User-added",
        DiscoveryMethod.Unresolved => "Unresolved",
        _ => m.ToString()
    };
}
