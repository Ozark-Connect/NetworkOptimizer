using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Client for polling TC (Traffic Control) statistics from UniFi gateways.
/// The gateway must have the tc-monitor script deployed, which exposes
/// SQM/FQ_CoDel rates via a simple HTTP endpoint on port 8088.
/// </summary>
public class TcMonitorClient
{
    private readonly ILogger<TcMonitorClient> _logger;
    private readonly HttpClient _httpClient;

    public const int DefaultPort = 8088;

    public TcMonitorClient(ILogger<TcMonitorClient> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("TcMonitor");
        _httpClient.Timeout = TimeSpan.FromSeconds(5);
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
        const int maxRetries = 3;
        const int retryDelayMs = 500;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.LogDebug("Polling TC stats from {Url} (attempt {Attempt}/{Max})", url, attempt, maxRetries);

                var response = await _httpClient.GetFromJsonAsync<TcMonitorResponse>(url);

                if (response != null)
                {
                    _logger.LogDebug("TC stats received: {InterfaceCount} interfaces",
                        response.Interfaces?.Count ?? 0);
                    return response;
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning("Failed to reach TC monitor at {Url} (attempt {Attempt}/{Max}): {Message}",
                    url, attempt, maxRetries, ex.Message);
            }
            catch (TaskCanceledException)
            {
                _logger.LogWarning("TC monitor request timed out for {Url} (attempt {Attempt}/{Max})",
                    url, attempt, maxRetries);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error polling TC monitor at {Url} (attempt {Attempt}/{Max})",
                    url, attempt, maxRetries);
            }

            // Wait before retrying (unless last attempt)
            if (attempt < maxRetries)
            {
                await Task.Delay(retryDelayMs);
            }
        }

        return null;
    }

    /// <summary>
    /// Check if a gateway has the tc-monitor script running
    /// </summary>
    public async Task<bool> IsMonitorAvailableAsync(string host, int port = DefaultPort)
    {
        var result = await GetTcStatsAsync(host, port);
        return result != null;
    }

    /// <summary>
    /// Get the primary WAN rate (first interface with active status)
    /// </summary>
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
    /// Get all interfaces, converting from legacy format if needed
    /// </summary>
    public List<TcInterfaceStats> GetAllInterfaces()
    {
        // If new format is present, use it
        if (Interfaces != null && Interfaces.Count > 0)
            return Interfaces;

        // Otherwise, convert from legacy wan1/wan2 format
        var result = new List<TcInterfaceStats>();

        if (Wan1 != null)
        {
            result.Add(new TcInterfaceStats
            {
                Name = Wan1.Name,
                Interface = Wan1.Interface,
                RateMbps = Wan1.RateMbps,
                RateRaw = Wan1.RateRaw,
                Status = Wan1.RateMbps > 0 ? "active" : "inactive"
            });
        }

        if (Wan2 != null)
        {
            result.Add(new TcInterfaceStats
            {
                Name = Wan2.Name,
                Interface = Wan2.Interface,
                RateMbps = Wan2.RateMbps,
                RateRaw = Wan2.RateRaw,
                Status = Wan2.RateMbps > 0 ? "active" : "inactive"
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
/// Legacy WAN stats format (for backwards compatibility)
/// </summary>
public class TcWanStats
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("interface")]
    public string Interface { get; set; } = "";

    [JsonPropertyName("rate_mbps")]
    public double RateMbps { get; set; }

    [JsonPropertyName("rate_raw")]
    public string? RateRaw { get; set; }
}
