using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NetworkOptimizer.Threats.CrowdSec;

/// <summary>
/// HTTP client for the CrowdSec CTI API (Smoke endpoint).
/// Feature-flagged: only active when enabled in settings with a valid API key.
/// </summary>
public class CrowdSecClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CrowdSecClient> _logger;
    private const string BaseUrl = "https://cti.api.crowdsec.net/v2/smoke/";
    private const int FreeTierDailyLimit = 50;
    private const int SafetyMargin = 5;

    // In-memory rate limit tracking (also persisted via SystemSettings)
    private int _requestsToday;
    private DateOnly _requestsDate = DateOnly.FromDateTime(DateTime.UtcNow);
    private readonly object _rateLimitLock = new();

    public CrowdSecClient(IHttpClientFactory httpClientFactory, ILogger<CrowdSecClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Load persisted rate limit state from SystemSettings on startup.
    /// </summary>
    public void LoadRateLimitState(int requestsToday, DateOnly requestsDate)
    {
        lock (_rateLimitLock)
        {
            _requestsToday = requestsToday;
            _requestsDate = requestsDate;
        }
    }

    /// <summary>
    /// Get current rate limit state for persistence.
    /// </summary>
    public (int RequestsToday, DateOnly RequestsDate) GetRateLimitState()
    {
        lock (_rateLimitLock)
        {
            return (_requestsToday, _requestsDate);
        }
    }

    /// <summary>
    /// Query the CrowdSec CTI Smoke API for an IP's reputation.
    /// Returns null if rate limited, disabled, or API error.
    /// </summary>
    public async Task<CrowdSecIpInfo?> GetIpReputationAsync(
        string ipAddress,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogDebug("CrowdSec API key not configured");
            return null;
        }

        if (!CheckAndIncrementRateLimit())
        {
            _logger.LogDebug("CrowdSec rate limit reached ({Requests}/{Limit} today)",
                _requestsToday, FreeTierDailyLimit);
            return null;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("CrowdSec");
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("x-api-key", apiKey);

            var response = await client.GetAsync($"{BaseUrl}{ipAddress}", cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // IP not in CrowdSec database - not an error
                _logger.LogDebug("IP {Ip} not found in CrowdSec database", ipAddress);
                return null;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning("CrowdSec API rate limit exceeded");
                // Force rate limit to prevent further requests today
                lock (_rateLimitLock)
                {
                    _requestsToday = FreeTierDailyLimit;
                }
                return null;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("CrowdSec API key is invalid or expired");
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<CrowdSecIpInfo>(cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CrowdSec API call failed for {Ip}", ipAddress);
            return null;
        }
    }

    /// <summary>
    /// Test the API key by making a request for a known test IP.
    /// </summary>
    public async Task<(bool Success, string Message)> TestApiKeyAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(apiKey))
            return (false, "API key is empty");

        try
        {
            var client = _httpClientFactory.CreateClient("CrowdSec");
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("x-api-key", apiKey);

            // Use a well-known IP for testing
            var response = await client.GetAsync($"{BaseUrl}1.1.1.1", cancellationToken);

            return response.StatusCode switch
            {
                HttpStatusCode.OK => (true, "API key is valid"),
                HttpStatusCode.Forbidden => (false, "API key is invalid or expired"),
                HttpStatusCode.TooManyRequests => (false, "Rate limit exceeded - try again tomorrow"),
                _ => (false, $"Unexpected response: {response.StatusCode}")
            };
        }
        catch (Exception ex)
        {
            return (false, $"Connection error: {ex.Message}");
        }
    }

    private bool CheckAndIncrementRateLimit()
    {
        lock (_rateLimitLock)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (_requestsDate != today)
            {
                _requestsDate = today;
                _requestsToday = 0;
            }

            if (_requestsToday >= FreeTierDailyLimit - SafetyMargin)
                return false;

            _requestsToday++;
            return true;
        }
    }
}
