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
    public override AuditSeverity Severity => AuditSeverity.Recommended;
    public override int ScoreImpact => 6;

    /// <summary>
    /// Maximum number of tagged VLANs before flagging as excessive.
    /// More than 2 tagged VLANs on a single-device port is unusual and
    /// may indicate misconfiguration or unnecessary VLAN exposure.
    /// </summary>
    private const int MaxTaggedVlansThreshold = 2;

    public override AuditIssue? Evaluate(PortInfo port, List<NetworkInfo> networks, List<NetworkInfo>? allNetworks = null)
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
        // We need either: connected client, single MAC restriction, or offline device data
        if (port.ConnectedClient == null &&
            !HasSingleDeviceMacRestriction(port) &&
            !HasOfflineDeviceData(port))
            return null;

        // At this point we have a trunk port with a single device attached
        // This is the misconfiguration we're looking for
        // Use allNetworks (including disabled) for VLAN counting - disabled networks are dormant config
        var networksForCounting = allNetworks ?? networks;
        if (networksForCounting.Count == 0)
            return null; // No networks to check

        // Calculate allowed tagged VLANs on this port (excluding native VLAN)
        var (taggedVlanCount, allowsAllVlans) = GetTaggedVlanInfo(port, networksForCounting);

        // Check if excessive
        if (!allowsAllVlans && taggedVlanCount <= MaxTaggedVlansThreshold)
            return null; // Within acceptable range

        // Build the issue - short message like other audit rules
        var network = GetNetwork(port.NativeNetworkId, networks);
        var vlanDesc = allowsAllVlans ? "all VLANs tagged" : $"{taggedVlanCount} VLANs tagged";

        return CreateIssue(
            $"Access port for single device has {vlanDesc}",
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
    /// Tagged VLANs = allowed networks minus native VLAN (native is untagged).
    /// </summary>
    private static (int TaggedVlanCount, bool AllowsAllVlans) GetTaggedVlanInfo(
        PortInfo port,
        List<NetworkInfo> networks)
    {
        var allNetworkIds = networks.Select(n => n.Id).ToHashSet();
        var excludedIds = port.ExcludedNetworkIds ?? new List<string>();
        var nativeNetworkId = port.NativeNetworkId;

        // If excluded list is null or empty, it means "Allow All"
        if (excludedIds.Count == 0)
        {
            // All networks minus native = tagged count
            var taggedCount = string.IsNullOrEmpty(nativeNetworkId)
                ? allNetworkIds.Count
                : allNetworkIds.Count - 1; // Subtract native
            return (taggedCount, true);
        }

        // Calculate allowed VLANs = All - Excluded - Native (if set)
        var allowedIds = allNetworkIds.Where(id => !excludedIds.Contains(id)).ToHashSet();

        // Remove native from tagged count (native is untagged, not tagged)
        if (!string.IsNullOrEmpty(nativeNetworkId))
        {
            allowedIds.Remove(nativeNetworkId);
        }

        return (allowedIds.Count, false);
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

    /// <summary>
    /// Check if port has MAC restriction with exactly 1 entry, indicating a single-device access port.
    /// </summary>
    private static bool HasSingleDeviceMacRestriction(PortInfo port)
    {
        return port.AllowedMacAddresses is { Count: 1 };
    }
}
