using System.Text.Json.Serialization;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// Response from GET /api/s/{site}/stat/portforward
/// Represents a port forwarding rule (both UPnP dynamic and static rules)
/// </summary>
public class UniFiPortForwardRule
{
    /// <summary>
    /// External port(s) - can be single port "80" or range "19132-19133" or list "80,443"
    /// </summary>
    [JsonPropertyName("dst_port")]
    public string? DstPort { get; set; }

    /// <summary>
    /// Internal IP address to forward to
    /// </summary>
    [JsonPropertyName("fwd")]
    public string? Fwd { get; set; }

    /// <summary>
    /// Internal port(s) - can be single port or range
    /// </summary>
    [JsonPropertyName("fwd_port")]
    public string? FwdPort { get; set; }

    /// <summary>
    /// 1 = UPnP rule, 0/null = static rule
    /// </summary>
    [JsonPropertyName("is_upnp")]
    public int? IsUpnp { get; set; }

    /// <summary>
    /// Seconds until UPnP lease expires (only for UPnP rules)
    /// </summary>
    [JsonPropertyName("lease_duration")]
    public int? LeaseDuration { get; set; }

    /// <summary>
    /// Rule name/description (e.g., "UPnP [Sunshine - RTSP]" or "Minecraft Server")
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Protocol: tcp, udp, or tcp_udp
    /// </summary>
    [JsonPropertyName("proto")]
    public string? Proto { get; set; }

    /// <summary>
    /// Traffic received through this rule (bytes)
    /// </summary>
    [JsonPropertyName("rx_bytes")]
    public long? RxBytes { get; set; }

    /// <summary>
    /// Packets received through this rule
    /// </summary>
    [JsonPropertyName("rx_packets")]
    public long? RxPackets { get; set; }

    /// <summary>
    /// Rule ID (only for static rules)
    /// </summary>
    [JsonPropertyName("_id")]
    public string? Id { get; set; }

    /// <summary>
    /// Whether the rule is enabled (only for static rules)
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    /// <summary>
    /// WAN interface: wan, wan2, etc. (only for static rules)
    /// </summary>
    [JsonPropertyName("pfwd_interface")]
    public string? PfwdInterface { get; set; }

    /// <summary>
    /// Whether logging is enabled for this rule (static rules only)
    /// </summary>
    [JsonPropertyName("log")]
    public bool? Log { get; set; }

    /// <summary>
    /// Source IP limiting type (static rules only)
    /// </summary>
    [JsonPropertyName("src_limiting_type")]
    public string? SrcLimitingType { get; set; }

    /// <summary>
    /// Whether source limiting is enabled (static rules only)
    /// </summary>
    [JsonPropertyName("src_limiting_enabled")]
    public bool? SrcLimitingEnabled { get; set; }

    /// <summary>
    /// Source firewall group ID for limiting (static rules only)
    /// </summary>
    [JsonPropertyName("src_firewall_group_id")]
    public string? SrcFirewallGroupId { get; set; }

    // Computed properties

    /// <summary>
    /// True if this is a static port forward rule (not UPnP)
    /// </summary>
    [JsonIgnore]
    public bool IsStatic => IsUpnp != 1;

    /// <summary>
    /// True if UPnP lease is expiring soon (less than 10 minutes)
    /// </summary>
    [JsonIgnore]
    public bool IsExpiringSoon => LeaseDuration.HasValue && LeaseDuration.Value < 600;

    /// <summary>
    /// True if any traffic has been received through this rule
    /// </summary>
    [JsonIgnore]
    public bool HasTraffic => (RxBytes ?? 0) > 0 || (RxPackets ?? 0) > 0;

    /// <summary>
    /// Formatted display of lease time remaining
    /// </summary>
    [JsonIgnore]
    public string LeaseTimeDisplay
    {
        get
        {
            if (!LeaseDuration.HasValue) return "N/A";
            var ts = TimeSpan.FromSeconds(LeaseDuration.Value);
            if (ts.TotalHours >= 1)
                return $"{(int)ts.TotalHours}h {ts.Minutes}m";
            if (ts.TotalMinutes >= 1)
                return $"{ts.Minutes}m {ts.Seconds}s";
            return $"{ts.Seconds}s";
        }
    }

    /// <summary>
    /// Protocol display (uppercase, TCP_UDP shown as TCP+UDP)
    /// </summary>
    [JsonIgnore]
    public string ProtoDisplay => Proto?.ToUpperInvariant() switch
    {
        "TCP_UDP" => "TCP+UDP",
        var p => p ?? "?"
    };

    /// <summary>
    /// Clean application name (strips "UPnP [" prefix and "]" suffix)
    /// </summary>
    [JsonIgnore]
    public string ApplicationName
    {
        get
        {
            if (string.IsNullOrEmpty(Name)) return "Unknown";
            if (Name.StartsWith("UPnP [") && Name.EndsWith("]"))
                return Name.Substring(6, Name.Length - 7);
            return Name;
        }
    }

    /// <summary>
    /// Formatted traffic display
    /// </summary>
    [JsonIgnore]
    public string TrafficDisplay
    {
        get
        {
            var bytes = RxBytes ?? 0;
            var packets = RxPackets ?? 0;
            if (bytes == 0 && packets == 0) return "No traffic";
            return $"{FormatBytes(bytes)} ({packets:N0} packets)";
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
