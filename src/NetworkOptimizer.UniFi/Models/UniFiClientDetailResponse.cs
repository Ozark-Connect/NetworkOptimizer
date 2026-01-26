using System.Text.Json.Serialization;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// Response from V2 client APIs: /clients/active and /clients/history
/// Used for both active clients (with current IP) and historical clients (with last_ip)
/// </summary>
public class UniFiClientDetailResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("mac")]
    public string Mac { get; set; } = string.Empty;

    [JsonPropertyName("site_id")]
    public string SiteId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("hostname")]
    public string? Hostname { get; set; }

    [JsonPropertyName("oui")]
    public string? Oui { get; set; }

    [JsonPropertyName("model_name")]
    public string? ModelName { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }  // "WIRED", "WIRELESS", "TELEPORT"

    [JsonPropertyName("status")]
    public string? Status { get; set; }  // "online", "offline"

    [JsonPropertyName("is_wired")]
    public bool IsWired { get; set; }

    [JsonPropertyName("is_guest")]
    public bool IsGuest { get; set; }

    [JsonPropertyName("blocked")]
    public bool Blocked { get; set; }

    // IP addresses
    [JsonPropertyName("ip")]
    public string? Ip { get; set; }

    [JsonPropertyName("last_ip")]
    public string? LastIp { get; set; }

    [JsonPropertyName("fixed_ip")]
    public string? FixedIp { get; set; }

    /// <summary>
    /// Gets the best available IP address (ip > last_ip > fixed_ip)
    /// </summary>
    [JsonIgnore]
    public string? BestIp => Ip ?? LastIp ?? FixedIp;

    [JsonPropertyName("use_fixedip")]
    public bool UseFixedIp { get; set; }

    // Network info (active clients use network_id/network_name, history uses last_connection_*)
    [JsonPropertyName("network_id")]
    public string? NetworkId { get; set; }

    [JsonPropertyName("network_name")]
    public string? NetworkName { get; set; }

    // Last connection info (history clients)
    [JsonPropertyName("last_uplink_mac")]
    public string? LastUplinkMac { get; set; }

    [JsonPropertyName("last_uplink_name")]
    public string? LastUplinkName { get; set; }

    [JsonPropertyName("last_uplink_remote_port")]
    public int? LastUplinkRemotePort { get; set; }

    [JsonPropertyName("last_connection_network_id")]
    public string? LastConnectionNetworkId { get; set; }

    [JsonPropertyName("last_connection_network_name")]
    public string? LastConnectionNetworkName { get; set; }

    // Timestamps (Unix epoch seconds)
    [JsonPropertyName("first_seen")]
    public long FirstSeen { get; set; }

    [JsonPropertyName("last_seen")]
    public long LastSeen { get; set; }

    // Fingerprint data (nested object)
    [JsonPropertyName("fingerprint")]
    public ClientFingerprintData? Fingerprint { get; set; }

    // Notes
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("noted")]
    public bool Noted { get; set; }

    // DNS
    [JsonPropertyName("local_dns_record")]
    public string? LocalDnsRecord { get; set; }

    [JsonPropertyName("local_dns_record_enabled")]
    public bool LocalDnsRecordEnabled { get; set; }

    // AP Lock settings
    /// <summary>
    /// Whether this client is locked to a specific AP.
    /// </summary>
    [JsonPropertyName("fixed_ap_enabled")]
    public bool? FixedApEnabled { get; set; }

    /// <summary>
    /// MAC address of the AP this client is locked to.
    /// Only relevant when FixedApEnabled is true.
    /// </summary>
    [JsonPropertyName("fixed_ap_mac")]
    public string? FixedApMac { get; set; }
}

/// <summary>
/// Fingerprint data from client history response
/// </summary>
public class ClientFingerprintData
{
    [JsonPropertyName("dev_cat")]
    public int? DevCat { get; set; }

    [JsonPropertyName("dev_family")]
    public int? DevFamily { get; set; }

    [JsonPropertyName("dev_id")]
    public int? DevId { get; set; }

    [JsonPropertyName("dev_vendor")]
    public int? DevVendor { get; set; }

    [JsonPropertyName("os_name")]
    public int? OsName { get; set; }

    [JsonPropertyName("os_class")]
    public int? OsClass { get; set; }

    [JsonPropertyName("has_override")]
    public bool HasOverride { get; set; }

    [JsonPropertyName("dev_id_override")]
    public int? DevIdOverride { get; set; }

    [JsonPropertyName("computed_dev_id")]
    public int? ComputedDevId { get; set; }

    [JsonPropertyName("computed_engine")]
    public int? ComputedEngine { get; set; }

    [JsonPropertyName("confidence")]
    public int? Confidence { get; set; }
}
