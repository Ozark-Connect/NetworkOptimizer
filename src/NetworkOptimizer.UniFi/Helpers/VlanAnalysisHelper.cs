using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.UniFi.Helpers;

/// <summary>
/// Helper methods for analyzing VLAN configurations on switch ports.
/// Resolves effective settings through port profiles and provides common analysis functions.
/// </summary>
public static class VlanAnalysisHelper
{
    /// <summary>
    /// Get effective VLAN settings for a port, resolving through port profile if assigned.
    /// Port profile settings override port's direct settings.
    /// </summary>
    /// <param name="port">The switch port to analyze</param>
    /// <param name="portOverride">Optional port override configuration</param>
    /// <param name="portProfile">The port profile if one is assigned to this port</param>
    /// <returns>Resolved effective VLAN settings</returns>
    public static EffectivePortVlanSettings GetEffectiveVlanSettings(
        SwitchPort port,
        PortOverride? portOverride,
        UniFiPortProfile? portProfile)
    {
        // Start with port's direct settings
        var settings = new EffectivePortVlanSettings
        {
            Forward = port.Forward,
            TaggedVlanMgmt = port.TaggedVlanMgmt,
            NativeNetworkId = port.NativeNetworkConfId,
            VoiceNetworkId = null,
            ExcludedNetworkIds = port.ExcludedNetworkConfIds ?? new List<string>(),
            ProfileName = null
        };

        // Port profile overrides (highest priority for assigned profiles)
        if (portProfile != null)
        {
            if (!string.IsNullOrEmpty(portProfile.Forward))
                settings.Forward = portProfile.Forward;

            if (!string.IsNullOrEmpty(portProfile.TaggedVlanMgmt))
                settings.TaggedVlanMgmt = portProfile.TaggedVlanMgmt;

            if (!string.IsNullOrEmpty(portProfile.NativeNetworkId))
                settings.NativeNetworkId = portProfile.NativeNetworkId;

            if (!string.IsNullOrEmpty(portProfile.VoiceNetworkId))
                settings.VoiceNetworkId = portProfile.VoiceNetworkId;

            if (portProfile.ExcludedNetworkConfIds?.Count > 0)
                settings.ExcludedNetworkIds = portProfile.ExcludedNetworkConfIds;

            settings.ProfileName = portProfile.Name;
        }

        // Port override can add additional tagged VLANs
        if (portOverride != null)
        {
            if (!string.IsNullOrEmpty(portOverride.VoiceNetworkConfId))
                settings.VoiceNetworkId = portOverride.VoiceNetworkConfId;

            if (portOverride.TaggedNetworkConfIds?.Count > 0)
                settings.AdditionalTaggedNetworkIds = portOverride.TaggedNetworkConfIds;
        }

        return settings;
    }

    /// <summary>
    /// Check if effective port settings indicate an access port (not a trunk).
    /// Access ports don't allow tagged VLANs except voice VLAN.
    /// </summary>
    public static bool IsAccessPort(EffectivePortVlanSettings settings)
    {
        return settings.Forward != "customize" || settings.TaggedVlanMgmt != "custom";
    }

    /// <summary>
    /// Check if effective port settings indicate a trunk port.
    /// Trunk ports allow multiple tagged VLANs.
    /// </summary>
    public static bool IsTrunkPort(EffectivePortVlanSettings settings)
    {
        return settings.Forward == "customize" && settings.TaggedVlanMgmt == "custom";
    }

    /// <summary>
    /// Get all tagged VLANs on an access port (voice + any additional explicit tags).
    /// Does not apply to trunk ports - use GetAllowedVlansOnTrunk for those.
    /// </summary>
    public static List<string> GetTaggedVlansOnAccessPort(EffectivePortVlanSettings settings)
    {
        var taggedVlans = new List<string>();

        // Voice VLAN is tagged
        if (!string.IsNullOrEmpty(settings.VoiceNetworkId))
            taggedVlans.Add(settings.VoiceNetworkId);

        // Additional explicitly tagged networks
        if (settings.AdditionalTaggedNetworkIds?.Count > 0)
            taggedVlans.AddRange(settings.AdditionalTaggedNetworkIds);

        return taggedVlans;
    }

    /// <summary>
    /// Calculate allowed VLANs on a trunk port given all available networks.
    /// Allowed VLANs = All Networks - ExcludedNetworkIds
    /// </summary>
    /// <param name="settings">The effective port settings</param>
    /// <param name="allNetworkIds">All available network IDs</param>
    /// <returns>Set of allowed VLAN network IDs</returns>
    public static HashSet<string> GetAllowedVlansOnTrunk(
        EffectivePortVlanSettings settings,
        IEnumerable<string> allNetworkIds)
    {
        var excludedSet = new HashSet<string>(settings.ExcludedNetworkIds);
        return allNetworkIds.Where(id => !excludedSet.Contains(id)).ToHashSet();
    }

    /// <summary>
    /// Determines if a network is a VLAN network (not WAN/VPN/system).
    /// Only VLAN networks are relevant for switch port VLAN analysis.
    /// </summary>
    /// <param name="network">The network configuration to check</param>
    /// <returns>True if this is a VLAN network that could be on switch ports</returns>
    public static bool IsVlanNetwork(UniFiNetworkConfig network)
    {
        // Filter based on network purpose
        var purpose = network.Purpose?.ToLowerInvariant() ?? "";

        // WAN networks are not VLANs on switch ports
        if (purpose == "wan")
            return false;

        // VPN networks (remote-user-vpn, site-vpn) are not switch port VLANs
        if (purpose.Contains("vpn"))
            return false;

        // vlan-only networks with no VLAN ID set might be system networks
        // Keep them if they have a VLAN ID
        if (purpose == "vlan-only" && network.Vlan == 0)
            return false;

        // Teleport/Tailscale virtual networks
        if (purpose == "teleport" || purpose == "tailscale")
            return false;

        // Include corporate, guest, vlan-only (with VLAN), and other LAN networks
        return true;
    }

    /// <summary>
    /// Check if a port is for network infrastructure (uplink/fabric).
    /// </summary>
    /// <param name="port">The switch port</param>
    /// <param name="deviceUplink">The device's uplink info</param>
    /// <returns>True if this port appears to be infrastructure</returns>
    public static bool IsInfrastructurePort(SwitchPort port, UplinkInfo? deviceUplink)
    {
        // Port is the device's own uplink
        if (deviceUplink?.UplinkRemotePort == port.PortIdx)
            return true;

        // Port name suggests infrastructure
        var nameLower = port.Name?.ToLowerInvariant() ?? "";
        return nameLower.Contains("uplink") ||
               nameLower.Contains("trunk") ||
               nameLower.Contains("backbone") ||
               nameLower.Contains("core");
    }

    /// <summary>
    /// Check if a port appears to be connected to a server based on port name.
    /// </summary>
    /// <param name="port">The switch port</param>
    /// <returns>True if port name suggests server connection</returns>
    public static bool IsServerPortByName(SwitchPort port)
    {
        var nameLower = port.Name?.ToLowerInvariant() ?? "";
        return nameLower.Contains("server") ||
               nameLower.Contains("esxi") ||
               nameLower.Contains("proxmox") ||
               nameLower.Contains("hypervisor") ||
               nameLower.Contains("nas") ||
               nameLower.Contains("storage");
    }
}

/// <summary>
/// Resolved effective VLAN settings for a port after applying profile overrides.
/// </summary>
public class EffectivePortVlanSettings
{
    /// <summary>
    /// Port forwarding mode: "customize" = trunk, "native" = access, "disabled" = disabled
    /// </summary>
    public string? Forward { get; set; }

    /// <summary>
    /// Tagged VLAN management: "custom" = trunk, "block_all" = access
    /// </summary>
    public string? TaggedVlanMgmt { get; set; }

    /// <summary>
    /// Native VLAN network config ID
    /// </summary>
    public string? NativeNetworkId { get; set; }

    /// <summary>
    /// Voice VLAN network config ID (tagged on access ports)
    /// </summary>
    public string? VoiceNetworkId { get; set; }

    /// <summary>
    /// Network IDs excluded from trunk (not allowed through)
    /// </summary>
    public List<string> ExcludedNetworkIds { get; set; } = new();

    /// <summary>
    /// Additional explicitly tagged networks (from port overrides)
    /// </summary>
    public List<string>? AdditionalTaggedNetworkIds { get; set; }

    /// <summary>
    /// Name of the port profile applied (for diagnostics)
    /// </summary>
    public string? ProfileName { get; set; }
}
