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

    public override AuditIssue? Evaluate(PortInfo port, List<NetworkInfo> networks, List<NetworkInfo>? allNetworks = null)
    {
        // Skip ports that are down AND have no recent activity.
        // Down ports with recent connections should still be checked - the unused port rule
        // won't flag them (within grace period), so MAC restriction is the right recommendation.
        // Truly inactive ports (no recent connection) will be caught by the unused port rule instead.
        if (!port.IsUp && !port.LastConnectionSeen.HasValue)
            return null;

        // Skip disabled ports - they're already secured
        if (port.ForwardMode == "disabled" || !port.IsEnabled)
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

        var network = GetNetwork(port.NativeNetworkId, networks);

        // Server networks: servers/hypervisors have multiple MACs from VMs and containers,
        // so MAC restriction is impractical. Recommend 802.1X multi-host if the switch supports it,
        // otherwise skip entirely.
        if (network?.Purpose == NetworkPurpose.Server)
        {
            if (port.IsDot1xSecured)
                return null; // Already secured via RADIUS

            if (port.Switch.Capabilities.Dot1xPortCtrlEnabled)
            {
                return CreateIssue(
                    "Server port should use 802.1X authentication for port security",
                    port,
                    new Dictionary<string, object>
                    {
                        { "network", network.Name }
                    },
                    "This port is on a Server network where MAC restriction is impractical due to multiple VM/container MACs. " +
                    "Use 802.1X Multi-Host mode to authenticate the server, then allow subsequent MACs on the port.");
            }

            return null; // Switch doesn't support 802.1X, nothing actionable
        }

        // Check if port already has MAC restrictions
        if (port.PortSecurityEnabled || (port.AllowedMacAddresses?.Any() ?? false))
            return null; // Already has restrictions

        // Skip ports secured via 802.1X/RADIUS authentication
        if (port.IsDot1xSecured)
            return null; // Already secured via RADIUS

        // Check if port has an intentional unrestricted profile assigned
        // (user has created an access port profile with MAC restriction explicitly disabled)
        if (HasIntentionalUnrestrictedProfile(port))
            return null;

        // Tailor the message based on whether the port is actively in use or just recently used
        var isInactive = !port.IsUp;

        var message = isInactive
            ? "Port is not in use - disable it, or add a MAC restriction if it's still needed"
            : "Port should be set to Restricted w/ an Allowed MAC Address or restricted via an Ethernet Port Profile in UniFi Network";

        var recommendation = isInactive
            ? "This port has no active connection. If it's no longer needed, set it to 'Disabled' in UniFi to prevent unauthorized access. " +
              "If it's still in use periodically, set it to 'Restricted' and add the device's MAC address to the allowed list."
            : "Enable MAC-based port security to prevent unauthorized devices from connecting. " +
              "In UniFi, set the port to 'Restricted' and add the device's MAC address to the allowed list. " +
              "If this port is intended to be used by multiple devices, create an Ethernet Port Profile with MAC restriction disabled and assign it to this port.";

        return CreateIssue(
            message,
            port,
            new Dictionary<string, object>
            {
                { "network", network?.Name ?? "Unknown" }
            },
            recommendation);
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
