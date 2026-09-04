using System.Text.Json.Serialization;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// GET v2/api/site/{site}/traffic: every client's WAN usage by DPI application over a window.
/// Directions are the client's own: <c>bytes_received</c> is what it downloaded.
/// </summary>
public class UniFiClientTrafficResponse
{
    [JsonPropertyName("client_usage_by_app")]
    public List<UniFiClientAppUsage> ClientUsageByApp { get; set; } = new();
}

public class UniFiClientAppUsage
{
    [JsonPropertyName("client")]
    public UniFiTrafficClient? Client { get; set; }

    [JsonPropertyName("usage_by_app")]
    public List<UniFiAppUsage> UsageByApp { get; set; } = new();
}

public class UniFiTrafficClient
{
    [JsonPropertyName("mac")]
    public string? Mac { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("hostname")]
    public string? Hostname { get; set; }

    [JsonPropertyName("is_wired")]
    public bool IsWired { get; set; }
}

public class UniFiAppUsage
{
    /// <summary>DPI application id within its category; see <see cref="DpiCatalog.Key"/>.</summary>
    [JsonPropertyName("application")]
    public int Application { get; set; }

    [JsonPropertyName("category")]
    public int Category { get; set; }

    [JsonPropertyName("bytes_received")]
    public long BytesReceived { get; set; }

    [JsonPropertyName("bytes_transmitted")]
    public long BytesTransmitted { get; set; }

    [JsonPropertyName("total_bytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("activity_seconds")]
    public long ActivitySeconds { get; set; }
}
