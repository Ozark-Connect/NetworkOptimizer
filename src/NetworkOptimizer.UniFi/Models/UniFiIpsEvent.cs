using System.Text.Json.Serialization;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// Response model for v1 API: GET stat/ips/event
/// Represents a single IPS/IDS alert from Suricata running on the UniFi gateway.
/// </summary>
public class UniFiIpsEvent
{
    [JsonPropertyName("_id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("src_ip")]
    public string? SrcIp { get; set; }

    [JsonPropertyName("src_port")]
    public int SrcPort { get; set; }

    [JsonPropertyName("dest_ip")]
    public string? DestIp { get; set; }

    [JsonPropertyName("dest_port")]
    public int DestPort { get; set; }

    [JsonPropertyName("proto")]
    public string? Proto { get; set; }

    [JsonPropertyName("catname")]
    public string? CatName { get; set; }

    [JsonPropertyName("in_iface")]
    public string? InIface { get; set; }

    [JsonPropertyName("alert")]
    public UniFiIpsAlert? Alert { get; set; }
}

public class UniFiIpsAlert
{
    [JsonPropertyName("signature_id")]
    public long SignatureId { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("severity")]
    public int Severity { get; set; }

    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("gid")]
    public int Gid { get; set; }

    [JsonPropertyName("rev")]
    public int Rev { get; set; }
}
