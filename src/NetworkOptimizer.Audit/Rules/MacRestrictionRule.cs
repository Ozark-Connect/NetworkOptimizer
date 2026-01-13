using NetworkOptimizer.Audit.Models;

namespace NetworkOptimizer.Audit.Rules;

/// <summary>
/// Detects access ports without MAC address restrictions.
/// MAC restrictions help prevent unauthorized device connections.
/// Excludes infrastructure ports (uplinks, WAN, ports with UniFi devices connected).
/// </summary>
public class MacRestrictionRule : AuditRuleBase
{
    public override string RuleId => "MAC-RESTRICT-001";
    public override string RuleName => "MAC Address Restriction";
    public override string Description => "Access ports should have MAC restrictions to prevent unauthorized devices";
    public override AuditSeverity Severity => AuditSeverity.Recommended;
    public override int ScoreImpact => 3;

    public override AuditIssue? Evaluate(PortInfo port, List<NetworkInfo> networks)
    {
        // Only check active ports
        if (!port.IsUp)
            return null;

        // Check if this is an access port (native or custom with native network set)
        var isAccessPort = port.ForwardMode == "native" ||
                           (port.ForwardMode == "custom" && !string.IsNullOrEmpty(port.NativeNetworkId));
        if (!isAccessPort)
            return null;

        // Skip infrastructure ports
        if (port.IsUplink || port.IsWan)
            return null;

        // Skip ports with network fabric devices (AP, switch, bridge) - these are LAN infrastructure
        // Modems, NVRs, Cloud Keys, etc. are endpoints and SHOULD get MAC restriction recommendations
        if (IsNetworkFabricDevice(port.ConnectedDeviceType))
            return null;

        // Fallback: check if port name suggests an AP (for cases where uplink data isn't available)
        if (IsAccessPointName(port.Name))
            return null;

        // Check if switch supports MAC ACLs
        if (port.Switch.Capabilities.MaxCustomMacAcls == 0)
            return null; // Switch doesn't support this feature

        // Check if port already has MAC restrictions
        if (port.PortSecurityEnabled || (port.AllowedMacAddresses?.Any() ?? false))
            return null; // Already has restrictions

        var network = GetNetwork(port.NativeNetworkId, networks);

        return CreateIssue(
            "No MAC restriction on access port",
            port,
            new Dictionary<string, object>
            {
                { "network", network?.Name ?? "Unknown" },
                { "recommendation", "Configure port security with allowed MAC addresses" }
            });
    }

    /// <summary>
    /// Check if the device type is network fabric (gateway, AP, switch, bridge) that shouldn't get MAC restriction recommendations.
    /// Modems, NVRs, Cloud Keys are endpoints and SHOULD get recommendations.
    /// </summary>
    private static bool IsNetworkFabricDevice(string? deviceType)
    {
        if (string.IsNullOrEmpty(deviceType))
            return false;

        // Only network fabric devices - the ones that carry LAN traffic
        return deviceType.ToLowerInvariant() switch
        {
            "ugw" or "usg" or "udm" or "uxg" or "ucg" => true,  // Gateways
            "uap" => true,  // Access Points
            "usw" => true,  // Switches
            "ubb" => true,  // Building-to-Building Bridges
            _ => false
        };
    }
}
