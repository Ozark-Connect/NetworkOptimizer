using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NetworkOptimizer.Threats.CrowdSec;

public enum CrowdSecLookupOutcome
{
    Success,
    NotFound,
    RateLimited,
    Error
}

/// <summary>
/// HTTP client for the CrowdSec CTI API (Smoke endpoint).
/// Feature-flagged: only active when enabled in settings with a valid API key.
/// </summary>
public class CrowdSecClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CrowdSecClient> _logger;
    private const string BaseUrl = "https://cti.api.crowdsec.net/v2/smoke/";
    private const int DefaultDailyLimit = 30;

    // In-memory rate limit tracking (also persisted via SystemSettings)
    private int _requestsToday;
    private int _dailyLimit = DefaultDailyLimit;
    private DateOnly _requestsDate = DateOnly.FromDateTime(DateTime.UtcNow);
    private DateTime? _rateLimitedUntil; // set when we receive an actual 429, expires after 1 hour
    private readonly object _rateLimitLock = new();

    public CrowdSecClient(IHttpClientFactory httpClientFactory, ILogger<CrowdSecClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Load persisted rate limit state from SystemSettings on startup.
    /// </summary>
    public void LoadRateLimitState(int requestsToday, DateOnly requestsDate, int dailyLimit = DefaultDailyLimit)
    {
        lock (_rateLimitLock)
        {
            _requestsToday = requestsToday;
            _requestsDate = requestsDate;
            _dailyLimit = dailyLimit >= 1 ? dailyLimit : DefaultDailyLimit;
        }
    }

    /// <summary>
    /// Get current rate limit state for persistence.
    /// </summary>
    public (int RequestsToday, DateOnly RequestsDate, int DailyLimit) GetRateLimitState()
    {
        lock (_rateLimitLock)
        {
            return (_requestsToday, _requestsDate, _dailyLimit);
        }
    }

    /// <summary>
    /// Whether the client has been rate-limited by CrowdSec recently (received a 429 within the last hour).
    /// </summary>
    public bool IsRateLimited
    {
        get
        {
            lock (_rateLimitLock)
            {
                return _rateLimitedUntil != null && DateTime.UtcNow < _rateLimitedUntil;
            }
        }
    }

    /// <summary>
    /// Query the CrowdSec CTI Smoke API for an IP's reputation.
    /// Returns the lookup result with an outcome indicating success, not-found, rate-limited, or error.
    /// </summary>
    public async Task<(CrowdSecIpInfo? Info, CrowdSecLookupOutcome Outcome)> GetIpReputationAsync(
        string ipAddress,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogDebug("CrowdSec API key not configured");
            return (null, CrowdSecLookupOutcome.Error);
        }

        if (!CheckAndIncrementRateLimit())
        {
            _logger.LogDebug("CrowdSec rate limit reached ({Requests}/{Limit} today)",
                _requestsToday, _dailyLimit);
            return (null, CrowdSecLookupOutcome.RateLimited);
        }

        try
        {
            var client = _httpClientFactory.CreateClient("CrowdSec");
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("x-api-key", apiKey);

            var response = await client.GetAsync($"{BaseUrl}{ipAddress}", cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogDebug("IP {Ip} not found in CrowdSec database", ipAddress);
                return (null, CrowdSecLookupOutcome.NotFound);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("CrowdSec API rate limit exceeded (429) after {RequestsToday}/{DailyLimit} requests today. Response: {Body}",
                    _requestsToday, _dailyLimit, body);
                lock (_rateLimitLock)
                {
                    _rateLimitedUntil = DateTime.UtcNow.AddHours(1);
                }
                return (null, CrowdSecLookupOutcome.RateLimited);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("CrowdSec API key is invalid or expired");
                return (null, CrowdSecLookupOutcome.Error);
            }

            response.EnsureSuccessStatusCode();
            var info = await response.Content.ReadFromJsonAsync<CrowdSecIpInfo>(cancellationToken: cancellationToken);
            return (info, CrowdSecLookupOutcome.Success);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CrowdSec API call failed for {Ip}", ipAddress);
            return (null, CrowdSecLookupOutcome.Error);
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
                HttpStatusCode.TooManyRequests => (false, "Rate limit exceeded - try again later"),
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

            // Only stop if CrowdSec actually returned 429 recently (within the last hour).
            // We don't know when CrowdSec resets their daily window, so back off for
            // 1 hour and try again rather than blocking the entire day.
            if (_rateLimitedUntil != null && DateTime.UtcNow < _rateLimitedUntil)
                return false;

            _requestsToday++;
            return true;
        }
    }
}
