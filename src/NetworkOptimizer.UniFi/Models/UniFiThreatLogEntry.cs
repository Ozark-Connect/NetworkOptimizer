using System.Text.Json.Serialization;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// Response model for v2 API: POST system-log/all with category THREAT_MANAGEMENT.
/// The v2 system-log format wraps IPS events in a different structure than stat/ips/event.
/// </summary>
public class UniFiThreatLogEntry
{
    [JsonPropertyName("_id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("time")]
    public long Time { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    [JsonPropertyName("subsystem")]
    public string? Subsystem { get; set; }

    [JsonPropertyName("category_name")]
    public string? CategoryName { get; set; }

    [JsonPropertyName("event_type")]
    public string? EventType { get; set; }

    [JsonPropertyName("inner_alert_action")]
    public string? InnerAlertAction { get; set; }

    [JsonPropertyName("inner_alert_category")]
    public string? InnerAlertCategory { get; set; }

    [JsonPropertyName("inner_alert_signature")]
    public string? InnerAlertSignature { get; set; }

    [JsonPropertyName("inner_alert_signature_id")]
    public long InnerAlertSignatureId { get; set; }

    [JsonPropertyName("inner_alert_severity")]
    public int InnerAlertSeverity { get; set; }

    [JsonPropertyName("src_ip")]
    public string? SrcIp { get; set; }

    [JsonPropertyName("src_port")]
    public int SrcPort { get; set; }

    [JsonPropertyName("dst_ip")]
    public string? DstIp { get; set; }

    [JsonPropertyName("dst_port")]
    public int DstPort { get; set; }

    [JsonPropertyName("proto")]
    public string? Proto { get; set; }

    [JsonPropertyName("host")]
    public string? Host { get; set; }
}
