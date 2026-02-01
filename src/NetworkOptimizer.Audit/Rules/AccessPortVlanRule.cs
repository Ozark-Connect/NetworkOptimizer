using NetworkOptimizer.Audit.Models;

namespace NetworkOptimizer.Audit.Rules;

/// <summary>
/// Detects access ports (single device attached) with excessive tagged VLANs.
/// Ports connected to a single end-user device should not have many tagged VLANs
/// or "Allow All" VLANs, as this exposes the port to unnecessary network access.
/// </summary>
public class AccessPortVlanRule : AuditRuleBase
{
    public override string RuleId => "ACCESS-VLAN-001";
    public override string RuleName => "Access Port VLAN Exposure";
    public override string Description => "Access ports should not have excessive tagged VLANs";
    public override AuditSeverity Severity => AuditSeverity.Critical;
    public override int ScoreImpact => 8;

    /// <summary>
    /// Maximum number of tagged VLANs before flagging as excessive.
    /// More than 2 tagged VLANs on a single-device port is unusual and
    /// may indicate misconfiguration or unnecessary VLAN exposure.
    /// </summary>
    private const int MaxTaggedVlansThreshold = 2;

    public override AuditIssue? Evaluate(PortInfo port, List<NetworkInfo> networks)
    {
        // Skip infrastructure ports
        if (port.IsUplink || port.IsWan)
            return null;

        // Only check ports configured as trunk/custom (these have tagged VLANs)
        // Access ports (ForwardMode = "native") don't have tagged VLANs - that's normal
        if (!IsTrunkPort(port.ForwardMode))
            return null;

        // Skip ports with network fabric devices (AP, switch, gateway, bridge)
        // These legitimately need multiple VLANs to serve downstream devices
        if (IsNetworkFabricDevice(port.ConnectedDeviceType))
            return null;

        // Skip if no single-device evidence
        // We need either a connected client or offline device data to confirm single device
        if (port.ConnectedClient == null && !HasOfflineDeviceData(port))
            return null;

        // At this point we have a trunk port with a single device attached
        // This is the misconfiguration we're looking for
        var vlanNetworks = networks.Where(n => n.VlanId > 0).ToList();
        if (vlanNetworks.Count == 0)
            return null; // No VLANs to check

        // Calculate allowed tagged VLANs on this port
        var (taggedVlanCount, allowsAllVlans) = GetTaggedVlanInfo(port, vlanNetworks);

        // Check if excessive
        if (!allowsAllVlans && taggedVlanCount <= MaxTaggedVlansThreshold)
            return null; // Within acceptable range

        // Build the issue
        var network = GetNetwork(port.NativeNetworkId, networks);
        var vlanDesc = allowsAllVlans ? "all VLANs" : $"{taggedVlanCount} tagged VLANs";

        return CreateIssue(
            $"Port with single device allows {vlanDesc} - configure to limit VLAN access",
            port,
            new Dictionary<string, object>
            {
                { "network", network?.Name ?? "Unknown" },
                { "tagged_vlan_count", taggedVlanCount },
                { "allows_all_vlans", allowsAllVlans },
                { "recommendation", "Limit tagged VLANs to only those required by this device" }
            });
    }

    /// <summary>
    /// Check if the port is configured as a trunk port (allows tagged VLANs).
    /// </summary>
    private static bool IsTrunkPort(string? forwardMode)
    {
        if (string.IsNullOrEmpty(forwardMode))
            return false;

        // "custom" and "customize" are trunk modes that allow tagged VLANs
        // "all" also allows all VLANs
        return forwardMode.Equals("custom", StringComparison.OrdinalIgnoreCase) ||
               forwardMode.Equals("customize", StringComparison.OrdinalIgnoreCase) ||
               forwardMode.Equals("all", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Get the tagged VLAN count and whether the port allows all VLANs.
    /// </summary>
    private static (int TaggedVlanCount, bool AllowsAllVlans) GetTaggedVlanInfo(
        PortInfo port,
        List<NetworkInfo> vlanNetworks)
    {
        var allVlanIds = vlanNetworks.Select(n => n.Id).ToHashSet();
        var excludedIds = port.ExcludedNetworkIds ?? new List<string>();

        // If excluded list is null or empty, it means "Allow All"
        if (excludedIds.Count == 0)
        {
            return (allVlanIds.Count, true);
        }

        // Calculate allowed VLANs = All - Excluded
        var allowedCount = allVlanIds.Count(id => !excludedIds.Contains(id));
        return (allowedCount, false);
    }

    /// <summary>
    /// Check if the device type is network fabric (gateway, AP, switch, bridge).
    /// These devices legitimately need trunk ports with multiple VLANs.
    /// </summary>
    private static bool IsNetworkFabricDevice(string? deviceType)
    {
        if (string.IsNullOrEmpty(deviceType))
            return false;

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
