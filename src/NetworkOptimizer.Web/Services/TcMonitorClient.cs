using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Client for polling TC (Traffic Control) statistics from UniFi gateways.
/// The gateway must have the tc-monitor script deployed, which exposes
/// SQM/FQ_CoDel rates via a simple HTTP endpoint on port 8088.
/// </summary>
public class TcMonitorClient : ITcMonitorClient
{
    private readonly ILogger<TcMonitorClient> _logger;

    public const int DefaultPort = 8088;

    public TcMonitorClient(ILogger<TcMonitorClient> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Poll TC statistics from a gateway running the tc-monitor script
    /// </summary>
    /// <param name="host">Gateway IP or hostname</param>
    /// <param name="port">Port number (default 8088)</param>
    /// <returns>TC monitor response with interface rates, or null if unreachable</returns>
    public async Task<TcMonitorResponse?> GetTcStatsAsync(string host, int port = DefaultPort)
    {
        var url = $"http://{host}:{port}/";

        try
        {
            _logger.LogDebug("Polling TC stats from {Url}", url);

            // Use simple HttpClient - no connection pooling issues
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await httpClient.GetFromJsonAsync<TcMonitorResponse>(url);

            if (response != null)
            {
                _logger.LogDebug("TC stats received: {InterfaceCount} interfaces",
                    response.Interfaces?.Count ?? 0);
                return response;
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Failed to reach TC monitor at {Url}: {Message}", url, ex.Message);
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("TC monitor request timed out for {Url}", url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling TC monitor at {Url}", url);
        }

        return null;
    }

    /// <summary>
    /// Check if a gateway has the tc-monitor script running.
    /// </summary>
    /// <param name="host">Gateway IP address or hostname.</param>
    /// <param name="port">Port number where tc-monitor is listening (default 8088).</param>
    /// <returns>True if the tc-monitor endpoint responds; otherwise, false.</returns>
    public async Task<bool> IsMonitorAvailableAsync(string host, int port = DefaultPort)
    {
        var result = await GetTcStatsAsync(host, port);
        return result != null;
    }

    /// <summary>
    /// Get the primary WAN rate (first interface with active status).
    /// </summary>
    /// <param name="host">Gateway IP address or hostname.</param>
    /// <param name="port">Port number where tc-monitor is listening (default 8088).</param>
    /// <returns>The rate in Mbps of the first active interface, or null if unavailable.</returns>
    public async Task<double?> GetPrimaryWanRateAsync(string host, int port = DefaultPort)
    {
        var stats = await GetTcStatsAsync(host, port);
        var primaryInterface = stats?.Interfaces?.FirstOrDefault(i => i.Status == "active");
        return primaryInterface?.RateMbps;
    }
}

/// <summary>
/// Response from the tc-monitor HTTP endpoint
/// </summary>
public class TcMonitorResponse
{
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("interfaces")]
    public List<TcInterfaceStats>? Interfaces { get; set; }

    // Legacy single-WAN properties for backwards compatibility
    [JsonPropertyName("wan1")]
    public TcWanStats? Wan1 { get; set; }

    [JsonPropertyName("wan2")]
    public TcWanStats? Wan2 { get; set; }

    /// <summary>
    /// Get all interfaces, converting from legacy wan1/wan2 format if necessary.
    /// </summary>
    /// <returns>List of interface statistics, preferring the new format if available.</returns>
    public List<TcInterfaceStats> GetAllInterfaces()
    {
        // If new format is present, use it
        if (Interfaces != null && Interfaces.Count > 0)
            return Interfaces;

        // Otherwise, convert from wan1/wan2 format
        var result = new List<TcInterfaceStats>();

        if (Wan1 != null)
        {
            result.Add(new TcInterfaceStats
            {
                Name = Wan1.Name,
                Interface = Wan1.Interface,
                RateMbps = Wan1.EffectiveRateMbps,
                RateRaw = Wan1.RateRaw,
                Status = Wan1.Active ? "active" : (Wan1.EffectiveRateMbps > 0 ? "active" : "inactive")
            });
        }

        if (Wan2 != null)
        {
            result.Add(new TcInterfaceStats
            {
                Name = Wan2.Name,
                Interface = Wan2.Interface,
                RateMbps = Wan2.EffectiveRateMbps,
                RateRaw = Wan2.RateRaw,
                Status = Wan2.Active ? "active" : (Wan2.EffectiveRateMbps > 0 ? "active" : "inactive")
            });
        }

        return result;
    }
}

/// <summary>
/// Statistics for a single TC-managed interface
/// </summary>
public class TcInterfaceStats
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("interface")]
    public string Interface { get; set; } = "";

    [JsonPropertyName("rate_mbps")]
    public double RateMbps { get; set; }

    [JsonPropertyName("rate_raw")]
    public string? RateRaw { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "unknown";
}

/// <summary>
/// WAN stats from SQM Monitor (includes speedtest/ping data)
/// </summary>
public class TcWanStats
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("interface")]
    public string Interface { get; set; } = "";

    [JsonPropertyName("active")]
    public bool Active { get; set; }

    // New SQM Monitor format
    [JsonPropertyName("current_rate_mbps")]
    public double CurrentRateMbps { get; set; }

    [JsonPropertyName("baseline_mbps")]
    public double BaselineMbps { get; set; }

    [JsonPropertyName("last_speedtest")]
    public SqmSpeedtestData? LastSpeedtest { get; set; }

    [JsonPropertyName("last_ping")]
    public SqmPingData? LastPing { get; set; }

    // Legacy format (for backwards compatibility with old tc-monitor)
    [JsonPropertyName("rate_mbps")]
    public double RateMbps { get; set; }

    [JsonPropertyName("rate_raw")]
    public string? RateRaw { get; set; }

    /// <summary>
    /// Get the effective rate (prefers new format, falls back to legacy)
    /// </summary>
    public double EffectiveRateMbps => CurrentRateMbps > 0 ? CurrentRateMbps : RateMbps;
}

/// <summary>
/// Speedtest data from SQM logs
/// </summary>
public class SqmSpeedtestData
{
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("measured_mbps")]
    public double MeasuredMbps { get; set; }

    [JsonPropertyName("adjusted_mbps")]
    public double AdjustedMbps { get; set; }
}

/// <summary>
/// Ping adjustment data from SQM logs
/// </summary>
public class SqmPingData
{
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("rate_mbps")]
    public double RateMbps { get; set; }

    [JsonPropertyName("latency_ms")]
    public double LatencyMs { get; set; }
}
