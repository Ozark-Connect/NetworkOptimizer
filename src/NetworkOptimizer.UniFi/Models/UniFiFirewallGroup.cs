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

    // Every group type stores its members here, including ipv6-address-group.
    // Absent (not []) when the group is empty, so this keeps its empty default.
    [JsonPropertyName("group_members")]
    public List<string> GroupMembers { get; set; } = new();

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }
}

/// <summary>
/// Body for POST rest/firewallgroup and PUT rest/firewallgroup/{id}, as UniFi Network's own UI
/// sends it. <c>_id</c> is null on create; on Network 11.x it is a UUID, on 10.x an ObjectId.
/// </summary>
public sealed record UniFiFirewallGroupWrite(
    [property: JsonPropertyName("_id")] string? Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("group_type")] string GroupType,
    [property: JsonPropertyName("group_members")] IReadOnlyList<string> GroupMembers,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("source")] string Source = "static");
