using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// Response from GET /api/s/{site}/stat/sta
/// Represents a connected client (wireless or wired)
/// </summary>
public class UniFiClientResponse
{
    [JsonPropertyName("_id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("mac")]
    public string Mac { get; set; } = string.Empty;

    [JsonPropertyName("site_id")]
    public string SiteId { get; set; } = string.Empty;

    [JsonPropertyName("is_guest")]
    public bool IsGuest { get; set; }

    [JsonPropertyName("is_wired")]
    public bool IsWired { get; set; }

    [JsonPropertyName("oui")]
    public string Oui { get; set; } = string.Empty;

    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("ip")]
    public string Ip { get; set; } = string.Empty;

    [JsonPropertyName("network")]
    public string Network { get; set; } = string.Empty;

    [JsonPropertyName("network_id")]
    public string NetworkId { get; set; } = string.Empty;

    [JsonPropertyName("use_fixedip")]
    public bool UseFixedIp { get; set; }

    [JsonPropertyName("fixed_ip")]
    public string? FixedIp { get; set; }

    // Connection info
    [JsonPropertyName("ap_mac")]
    public string? ApMac { get; set; }

    [JsonPropertyName("sw_mac")]
    public string? SwMac { get; set; }

    [JsonPropertyName("sw_port")]
    public int? SwPort { get; set; }

    [JsonPropertyName("sw_depth")]
    public int? SwDepth { get; set; }

    [JsonPropertyName("uptime")]
    public long Uptime { get; set; }

    [JsonPropertyName("last_seen")]
    public long LastSeen { get; set; }

    [JsonPropertyName("first_seen")]
    public long FirstSeen { get; set; }

    // Wireless-specific
    [JsonPropertyName("essid")]
    public string? Essid { get; set; }

    [JsonPropertyName("bssid")]
    public string? Bssid { get; set; }

    [JsonPropertyName("channel")]
    public int? Channel { get; set; }

    [JsonPropertyName("radio")]
    public string? Radio { get; set; }

    [JsonPropertyName("radio_proto")]
    public string? RadioProto { get; set; }

    [JsonPropertyName("rssi")]
    public int? Rssi { get; set; }

    [JsonPropertyName("signal")]
    public int? Signal { get; set; }

    [JsonPropertyName("noise")]
    public int? Noise { get; set; }

    // Traffic stats
    [JsonPropertyName("tx_bytes")]
    public long TxBytes { get; set; }

    [JsonPropertyName("rx_bytes")]
    public long RxBytes { get; set; }

    [JsonPropertyName("tx_packets")]
    public long TxPackets { get; set; }

    [JsonPropertyName("rx_packets")]
    public long RxPackets { get; set; }

    [JsonPropertyName("tx_rate")]
    public long TxRate { get; set; }

    [JsonPropertyName("rx_rate")]
    public long RxRate { get; set; }

    [JsonPropertyName("tx_bytes-r")]
    public double TxBytesRate { get; set; }

    [JsonPropertyName("rx_bytes-r")]
    public double RxBytesRate { get; set; }

    // QoS and experience
    [JsonPropertyName("qos_policy_applied")]
    public bool QosPolicyApplied { get; set; }

    [JsonPropertyName("satisfaction")]
    public int Satisfaction { get; set; }

    [JsonPropertyName("anomalies")]
    public int Anomalies { get; set; }

    // User info
    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }

    [JsonPropertyName("usergroup_id")]
    public string? UsergroupId { get; set; }

    [JsonPropertyName("noted")]
    public bool Noted { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    // Device fingerprinting (API returns varying types: int, string, or array)
    [JsonPropertyName("fingerprint_source")]
    public JsonElement? FingerprintSource { get; set; }

    [JsonPropertyName("dev_id_override")]
    public int DevIdOverride { get; set; }

    [JsonPropertyName("dev_cat")]
    public int DevCat { get; set; }

    [JsonPropertyName("dev_family")]
    public int DevFamily { get; set; }

    [JsonPropertyName("os_class")]
    public int OsClass { get; set; }

    [JsonPropertyName("os_name")]
    public int OsName { get; set; }

    [JsonPropertyName("dev_vendor")]
    public int DevVendor { get; set; }

    // Blocked/allowed status
    [JsonPropertyName("blocked")]
    public bool Blocked { get; set; }
}
