using System.Text.Json.Serialization;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// Represents a UniFi port profile from /rest/portconf endpoint.
/// Port profiles define configuration templates that can be applied to switch ports.
/// When a port has a portconf_id, its settings come from the profile rather than the port itself.
/// </summary>
public class UniFiPortProfile
{
    [JsonPropertyName("_id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("site_id")]
    public string? SiteId { get; set; }

    [JsonPropertyName("forward")]
    public string? Forward { get; set; }

    [JsonPropertyName("native_networkconf_id")]
    public string? NativeNetworkId { get; set; }

    [JsonPropertyName("voice_networkconf_id")]
    public string? VoiceNetworkId { get; set; }

    [JsonPropertyName("port_security_enabled")]
    public bool PortSecurityEnabled { get; set; }

    [JsonPropertyName("port_security_mac_address")]
    public List<string>? PortSecurityMacAddresses { get; set; }

    [JsonPropertyName("isolation")]
    public bool Isolation { get; set; }

    [JsonPropertyName("poe_mode")]
    public string? PoeMode { get; set; }

    [JsonPropertyName("op_mode")]
    public string? OpMode { get; set; }

    [JsonPropertyName("autoneg")]
    public bool Autoneg { get; set; }

    [JsonPropertyName("setting_preference")]
    public string? SettingPreference { get; set; }

    [JsonPropertyName("tagged_vlan_mgmt")]
    public string? TaggedVlanMgmt { get; set; }

    /// <summary>
    /// Network config IDs excluded from this trunk profile.
    /// Only relevant for trunk port profiles.
    /// Allowed VLANs = All Networks - ExcludedNetworkConfIds
    /// </summary>
    [JsonPropertyName("excluded_networkconf_ids")]
    public List<string>? ExcludedNetworkConfIds { get; set; }

    [JsonPropertyName("stormctrl_bcast_enabled")]
    public bool StormCtrlBcastEnabled { get; set; }

    [JsonPropertyName("stormctrl_mcast_enabled")]
    public bool StormCtrlMcastEnabled { get; set; }

    [JsonPropertyName("stormctrl_ucast_enabled")]
    public bool StormCtrlUcastEnabled { get; set; }
}
