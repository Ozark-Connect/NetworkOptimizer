using System.Text.Json.Serialization;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// Response from GET /api/s/{site}/rest/firewallgroup
/// Represents a firewall group (address, IPv6 address, port, or domain group)
/// </summary>
public class UniFiFirewallGroup
{
    [JsonPropertyName("_id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("site_id")]
    public string SiteId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("group_type")]
    public string GroupType { get; set; } = string.Empty; // "address-group", "ipv6-address-group", "port-group", "domain-group"

    // Every group type stores its members here, including ipv6-address-group
    [JsonPropertyName("group_members")]
    public List<string> GroupMembers { get; set; } = new();
}
