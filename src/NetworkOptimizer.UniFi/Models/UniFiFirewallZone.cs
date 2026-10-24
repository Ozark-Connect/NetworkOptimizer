using System.Text.Json.Serialization;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// Response from GET /proxy/network/v2/api/site/{site}/firewall/zone
/// Represents a firewall zone configuration.
///
/// UniFi has predefined zone types identified by zone_key:
/// - internal: Default zone for LAN networks
/// - external: WAN/Internet zone
/// - gateway: Gateway services zone
/// - vpn: VPN zone
/// - hotspot: Guest/Hotspot networks zone
/// - dmz: DMZ networks zone
/// </summary>
public class UniFiFirewallZone
{
    [JsonPropertyName("_id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The zone type key. Known values:
    /// - "internal" - Default LAN zone
    /// - "external" - WAN/Internet zone
    /// - "gateway" - Gateway services
    /// - "vpn" - VPN zone
    /// - "hotspot" - Guest/Hotspot networks
    /// - "dmz" - DMZ networks
    /// </summary>
    [JsonPropertyName("zone_key")]
    public string ZoneKey { get; set; } = string.Empty;

    /// <summary>
    /// List of network IDs assigned to this zone.
    /// </summary>
    [JsonPropertyName("network_ids")]
    public List<string> NetworkIds { get; set; } = [];

    /// <summary>
    /// Whether this is a default/system zone.
    /// </summary>
    [JsonPropertyName("default_zone")]
    public bool IsDefaultZone { get; set; }

    /// <summary>
    /// Whether this zone can be edited.
    /// System zones like External, Gateway, VPN have attr_no_edit=true.
    /// </summary>
    [JsonPropertyName("attr_no_edit")]
    public bool IsReadOnly { get; set; }

    [JsonPropertyName("external_id")]
    public string? ExternalId { get; set; }

    [JsonPropertyName("site_id")]
    public string? SiteId { get; set; }
}

/// <summary>
/// A peer of a WireGuard server network (GET v2/api/site/{site}/wireguard/{networkId}/users). Its
/// traffic arrives in the server network's zone, from its interface address or a subnet it routes.
/// </summary>
public class UniFiWireGuardPeer
{
    [JsonPropertyName("_id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("network_id")] public string NetworkId { get; set; } = string.Empty;
    [JsonPropertyName("interface_ip")] public string? InterfaceIp { get; set; }
    [JsonPropertyName("interface_ipv6")] public string? InterfaceIpv6 { get; set; }
    /// <summary>The subnets behind the peer it routes (a site-to-site peer's LAN); empty for a single client.</summary>
    [JsonPropertyName("allowed_ips")] public List<string> AllowedIps { get; set; } = [];
}

/// <summary>
/// Well-known zone key constants for type-safe zone identification.
/// </summary>
public static class FirewallZoneKeys
{
    public const string Internal = "internal";
    public const string External = "external";
    public const string Gateway = "gateway";
    public const string Vpn = "vpn";
    public const string Hotspot = "hotspot";
    public const string Dmz = "dmz";
}
