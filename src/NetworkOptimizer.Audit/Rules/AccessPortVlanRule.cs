using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Enums;

using AuditSeverity = NetworkOptimizer.Audit.Models.AuditSeverity;

namespace NetworkOptimizer.Audit.Rules;

/// <summary>
/// Detects trunk ports with excessive tagged VLANs that appear to be access ports.
/// Trunk ports with no device connected or only a single device should not have many
/// tagged VLANs or "Allow All" VLANs, as this exposes the port to unnecessary network access.
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

    /// <summary>
    /// Higher threshold for server/hypervisor devices (Proxmox, ESXi, TrueNAS, etc.).
    /// These devices legitimately need multiple tagged VLANs to serve VMs/containers
    /// on different networks, so a higher threshold avoids false positives.
    /// </summary>
    private const int MaxServerTaggedVlansThreshold = 5;

    /// <summary>
    /// Device categories that are considered servers/hypervisors and get the higher VLAN threshold.
    /// </summary>
    private static readonly HashSet<ClientDeviceCategory> ServerCategories = new()
    {
        ClientDeviceCategory.Server,
        ClientDeviceCategory.NAS
    };

    public override AuditIssue? Evaluate(PortInfo port, List<NetworkInfo> networks, List<NetworkInfo>? allNetworks = null)
    {
        // Skip infrastructure ports
        if (port.IsUplink || port.IsWan)
            return null;

        // Only check ports configured as trunk/custom (these have tagged VLANs)
        // Access ports (ForwardMode = "native") don't have tagged VLANs - that's normal
        // Ports with tagged_vlan_mgmt = "block_all" block all tagged VLANs regardless of forward mode
        if (!IsTrunkPort(port.ForwardMode, port.TaggedVlanMgmt))
            return null;

        // Skip ports with network fabric devices (AP, switch, gateway, bridge)
        // These legitimately need multiple VLANs to serve downstream devices
        if (IsNetworkFabricDevice(port.ConnectedDeviceType))
            return null;

        // Check if we have evidence of a single device attached
        // (connected client, single MAC restriction, or offline device data)
        var hasSingleDeviceEvidence = port.ConnectedClient != null ||
            HasSingleDeviceMacRestriction(port) ||
            HasOfflineDeviceData(port);

        // Detect if the connected device is a server/hypervisor (Proxmox, ESXi, TrueNAS, etc.)
        // These legitimately need more tagged VLANs for VMs/containers on different networks
        var isServerDevice = false;
        if (hasSingleDeviceEvidence)
        {
            var detection = DetectDeviceType(port);
            isServerDevice = ServerCategories.Contains(detection.Category);
        }

        var effectiveThreshold = isServerDevice ? MaxServerTaggedVlansThreshold : MaxTaggedVlansThreshold;

        // At this point we have a trunk port that either:
        // - Has a single device attached (misconfigured access port)
        // - Has no device evidence (unused trunk port that should be disabled or reconfigured)
        // Use allNetworks (including disabled) for VLAN counting - disabled networks are dormant config
        var networksForCounting = allNetworks ?? networks;
        if (networksForCounting.Count == 0)
            return null; // No networks to check

        // Calculate allowed tagged VLANs on this port (excluding native VLAN)
        var (taggedVlanCount, allowsAllVlans) = GetTaggedVlanInfo(port, networksForCounting);

        // Check if excessive
        if (!allowsAllVlans && taggedVlanCount <= effectiveThreshold)
            return null; // Within acceptable range

        // Build the issue - short message like other audit rules
        var network = GetNetwork(port.NativeNetworkId, networks);
        var vlanDesc = allowsAllVlans ? "all VLANs tagged" : $"{taggedVlanCount} VLANs tagged";

        // Build message and recommendation based on device evidence
        string message;
        string recommendation;

        if (hasSingleDeviceEvidence)
        {
            if (isServerDevice)
            {
                // Server/hypervisor with excessive VLANs
                message = $"Server port has {vlanDesc}";
                recommendation = allowsAllVlans
                    ? "Configure the port to allow only the specific VLANs this server's VMs/containers require. " +
                      "'Allow All' automatically exposes any new VLANs added to your network."
                    : $"This server port has {taggedVlanCount} tagged VLANs. " +
                      "Restrict tagged VLANs to only those required by the server's VMs or containers.";
            }
            else
            {
                // Single device attached - misconfigured access port
                message = $"Access port for single device has {vlanDesc}";
                recommendation = allowsAllVlans
                    ? "Configure the port to allow only the specific VLANs this device requires. " +
                      "'Allow All' automatically exposes any new VLANs added to your network."
                    : $"This single-device port has {taggedVlanCount} tagged VLANs. " +
                      "Most devices only need their native VLAN - restrict tagged VLANs to those actually required.";
            }
        }
        else
        {
            // No device evidence - unused trunk port
            message = $"Trunk port with no device has {vlanDesc}";
            recommendation = allowsAllVlans
                ? "This port has no connected device but allows all VLANs. " +
                  "Disable the port or configure it as an access port with only the required VLAN."
                : $"This port has no connected device but has {taggedVlanCount} tagged VLANs. " +
                  "Disable the port or configure it as an access port with only the required VLAN.";
        }

        return CreateIssue(
            message,
            port,
            new Dictionary<string, object>
            {
                { "network", network?.Name ?? "Unknown" },
                { "tagged_vlan_count", taggedVlanCount },
                { "allows_all_vlans", allowsAllVlans },
                { "has_device_evidence", hasSingleDeviceEvidence },
                { "is_server_device", isServerDevice }
            },
            recommendation);
    }

    /// <summary>
    /// Check if the port is configured as a trunk port (allows tagged VLANs).
    /// A port with tagged_vlan_mgmt = "block_all" blocks all tagged VLANs
    /// regardless of forward mode, so it's effectively an access port.
    /// </summary>
    private static bool IsTrunkPort(string? forwardMode, string? taggedVlanMgmt)
    {
        if (string.IsNullOrEmpty(forwardMode))
            return false;

        // "block_all" means all tagged VLANs are blocked - port is effectively access-only
        if (string.Equals(taggedVlanMgmt, "block_all", StringComparison.OrdinalIgnoreCase))
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
