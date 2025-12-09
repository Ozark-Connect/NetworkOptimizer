using System.Text.Json.Serialization;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// Response from GET /api/s/{site}/rest/firewallrule
/// Represents a firewall rule in UniFi controller
/// </summary>
public class UniFiFirewallRule
{
    [JsonPropertyName("_id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("site_id")]
    public string SiteId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty; // "accept", "drop", "reject"

    [JsonPropertyName("ruleset")]
    public string Ruleset { get; set; } = string.Empty; // "WAN_IN", "WAN_OUT", "WAN_LOCAL", "LAN_IN", "LAN_OUT", "LAN_LOCAL", "GUEST_IN", "GUEST_OUT", "GUEST_LOCAL"

    [JsonPropertyName("rule_index")]
    public int RuleIndex { get; set; }

    // Protocol and port configuration
    [JsonPropertyName("protocol_match_excepted")]
    public bool ProtocolMatchExcepted { get; set; }

    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = string.Empty; // "tcp", "udp", "tcp_udp", "icmp", "all"

    [JsonPropertyName("icmp_typename")]
    public string? IcmpTypename { get; set; }

    // Source configuration
    [JsonPropertyName("src_firewallgroup_ids")]
    public List<string>? SrcFirewallGroupIds { get; set; }

    [JsonPropertyName("src_mac_address")]
    public string? SrcMacAddress { get; set; }

    [JsonPropertyName("src_address")]
    public string? SrcAddress { get; set; }

    [JsonPropertyName("src_networkconf_id")]
    public string? SrcNetworkconfId { get; set; }

    [JsonPropertyName("src_networkconf_type")]
    public string? SrcNetworkconfType { get; set; }

    // Destination configuration
    [JsonPropertyName("dst_firewallgroup_ids")]
    public List<string>? DstFirewallGroupIds { get; set; }

    [JsonPropertyName("dst_address")]
    public string? DstAddress { get; set; }

    [JsonPropertyName("dst_networkconf_id")]
    public string? DstNetworkconfId { get; set; }

    [JsonPropertyName("dst_networkconf_type")]
    public string? DstNetworkconfType { get; set; }

    // Port configuration
    [JsonPropertyName("dst_port")]
    public string? DstPort { get; set; }

    [JsonPropertyName("src_port")]
    public string? SrcPort { get; set; }

    // Logging and state
    [JsonPropertyName("logging")]
    public bool Logging { get; set; }

    [JsonPropertyName("state_established")]
    public bool StateEstablished { get; set; }

    [JsonPropertyName("state_invalid")]
    public bool StateInvalid { get; set; }

    [JsonPropertyName("state_new")]
    public bool StateNew { get; set; }

    [JsonPropertyName("state_related")]
    public bool StateRelated { get; set; }

    // IPsec
    [JsonPropertyName("ipsec")]
    public string? Ipsec { get; set; }

    // Scheduling
    [JsonPropertyName("schedule")]
    public string? Schedule { get; set; }

    // Traffic control
    [JsonPropertyName("bandwidth_limit")]
    public BandwidthLimit? BandwidthLimit { get; set; }
}

public class BandwidthLimit
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("download_limit_kbps")]
    public int DownloadLimitKbps { get; set; }

    [JsonPropertyName("upload_limit_kbps")]
    public int UploadLimitKbps { get; set; }
}
