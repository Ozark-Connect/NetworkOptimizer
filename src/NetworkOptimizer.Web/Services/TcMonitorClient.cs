using System.Text.Json.Serialization;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Client for polling TC (Traffic Control) statistics from UniFi gateways.
/// The gateway must have the tc-monitor script deployed, which exposes
/// SQM/FQ_CoDel rates via a simple HTTP endpoint on port 8088.
/// </summary>
public class TcMonitorClient : ITcMonitorClient
{
    private readonly ILogger<TcMonitorClient> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SiteTunnelRouting _tunnelRouting;

    public const int DefaultPort = 8088;

    // Per-endpoint cache + request gate, keyed by URL. Caching avoids hammering the
    // single-threaded TC Monitor server, and the gate serializes requests because the
    // netcat-based server can only handle one connection at a time - but both are per
    // gateway, so with multiple sites each endpoint gets its own slot: one site's
    // polling never evicts another's cache or blocks its requests.
    private sealed class EndpointState
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public TcMonitorResponse? Response;
        public DateTime CacheTime = DateTime.MinValue;
        // Expire stale cache after consecutive failures (prevents showing data from a dead monitor)
        public int ConsecutiveFailures;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, EndpointState> _endpoints = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    private const int MaxConsecutiveFailures = 3;

    public TcMonitorClient(ILogger<TcMonitorClient> logger, IHttpClientFactory httpClientFactory, SiteTunnelRouting tunnelRouting)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _tunnelRouting = tunnelRouting;
    }

    /// <summary>
    /// Poll TC statistics from a gateway running the tc-monitor script.
    /// </summary>
    /// <param name="host">Gateway IP or hostname</param>
    /// <param name="port">Port number (default 8088)</param>
    /// <param name="forceRefresh">Bypass cache and fetch fresh data</param>
    /// <returns>TC monitor response with interface rates, or null if unreachable</returns>
    public async Task<TcMonitorResponse?> GetTcStatsAsync(string host, int port = DefaultPort, bool forceRefresh = false, string? siteSlug = null)
    {
        // A non-default agent-backed site reaches its gateway through the tunnel:
        // rewrite host:port to the loopback proxy endpoint before polling. The
        // default site (or a null slug) routes directly with no extra DB hit -
        // RouteAsync short-circuits before touching the site's settings.
        if (!string.IsNullOrEmpty(siteSlug))
            (host, port) = await _tunnelRouting.RouteAsync(siteSlug, host, port);

        var url = $"http://{host}:{port}/";
        var state = _endpoints.GetOrAdd(url, _ => new EndpointState());

        // Return cached if valid and not forcing refresh
        if (!forceRefresh && state.Response != null && DateTime.UtcNow - state.CacheTime < CacheDuration)
        {
            _logger.LogDebug("Returning cached TC stats (age: {Age:F1}s)", (DateTime.UtcNow - state.CacheTime).TotalSeconds);
            return state.Response;
        }

        // Serialize requests to this endpoint - if another is in progress, return cached data
        if (!await state.Gate.WaitAsync(TimeSpan.FromMilliseconds(100)))
        {
            _logger.LogDebug("TC monitor request already in progress, returning cached data");
            return state.Response;
        }

        try
        {
            // Double-check cache after acquiring lock
            if (!forceRefresh && state.Response != null && DateTime.UtcNow - state.CacheTime < CacheDuration)
            {
                return state.Response;
            }

            // Retry once on failure (netcat server briefly unavailable between requests)
            const int maxAttempts = 2;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    _logger.LogDebug("Polling TC stats from {Url} (attempt {Attempt}/{Max})", url, attempt, maxAttempts);

                    using var httpClient = _httpClientFactory.CreateClient("TcMonitor");
                    httpClient.Timeout = TimeSpan.FromSeconds(5);
                    var response = await httpClient.GetFromJsonAsync<TcMonitorResponse>(url);

                    if (response != null)
                    {
                        _logger.LogDebug("TC stats received: {InterfaceCount} interfaces", response.GetAllInterfaces().Count);
                        state.Response = response;
                        state.CacheTime = DateTime.UtcNow;
                        state.ConsecutiveFailures = 0;
                        return response;
                    }
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogDebug("TC monitor attempt {Attempt} failed: {Message}", attempt, ex.Message);
                    if (attempt < maxAttempts)
                    {
                        await Task.Delay(500);
                        continue;
                    }
                    _logger.LogWarning("Failed to reach TC monitor at {Url}: {Message}", url, ex.Message);
                }
                catch (TaskCanceledException)
                {
                    _logger.LogWarning("TC monitor request timed out for {Url}", url);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error polling TC monitor at {Url}", url);
                    break;
                }
            }

            state.ConsecutiveFailures++;
            if (state.ConsecutiveFailures >= MaxConsecutiveFailures)
            {
                _logger.LogDebug("TC monitor unreachable after {Failures} attempts, expiring stale cache", state.ConsecutiveFailures);
                state.Response = null;
                return null;
            }

            return state.Response;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    /// <summary>
    /// Check if a gateway has the tc-monitor script running.
    /// </summary>
    /// <param name="host">Gateway IP address or hostname.</param>
    /// <param name="port">Port number where tc-monitor is listening (default 8088).</param>
    /// <returns>True if the tc-monitor endpoint responds; otherwise, false.</returns>
    public async Task<bool> IsMonitorAvailableAsync(string host, int port = DefaultPort, string? siteSlug = null)
    {
        var result = await GetTcStatsAsync(host, port, siteSlug: siteSlug);
        return result != null;
    }

    /// <summary>
    /// Get the primary WAN rate (first interface with active status).
    /// </summary>
    /// <param name="host">Gateway IP address or hostname.</param>
    /// <param name="port">Port number where tc-monitor is listening (default 8088).</param>
    /// <returns>The rate in Mbps of the first active interface, or null if unavailable.</returns>
    public async Task<double?> GetPrimaryWanRateAsync(string host, int port = DefaultPort, string? siteSlug = null)
    {
        var stats = await GetTcStatsAsync(host, port, siteSlug: siteSlug);
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

    [JsonPropertyName("speedtest_running")]
    public bool SpeedtestRunning { get; set; }

    [JsonPropertyName("last_error")]
    public SqmErrorData? LastError { get; set; }

    // Legacy format (for backwards compatibility with old tc-monitor)
    [JsonPropertyName("rate_mbps")]
    public double RateMbps { get; set; }

    [JsonPropertyName("rate_raw")]
    public string? RateRaw { get; set; }

    /// <summary>
    /// Get the effective rate (prefers new format, falls back to legacy)
    /// </summary>
    public double EffectiveRateMbps => CurrentRateMbps > 0 ? CurrentRateMbps : RateMbps;

    /// <summary>
    /// True if the last speedtest appears to have failed (measured 0 Mbps).
    /// This indicates the speedtest CLI didn't return valid data.
    /// </summary>
    public bool LastSpeedtestFailed => LastSpeedtest != null && LastSpeedtest.MeasuredMbps == 0 && !SpeedtestRunning;
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

/// <summary>
/// Error data from SQM logs (e.g., IFB device missing)
/// </summary>
public class SqmErrorData
{
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
