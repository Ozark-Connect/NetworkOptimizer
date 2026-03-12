using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NetworkOptimizer.Threats.CrowdSec;

public enum CrowdSecLookupOutcome
{
    Success,
    NotFound,
    /// <summary>Daily quota exhausted ("Limit Exceeded"). No more calls today.</summary>
    QuotaExhausted,
    /// <summary>Burst throttle ("Too Many Requests"). Transient - caller should retry later.</summary>
    BurstThrottled,
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
    private const int MinRequestIntervalMs = 500;
    private const int MaxBurstBackoffMs = 30_000;

    // In-memory rate limit tracking (also persisted via SystemSettings)
    private int _requestsToday;
    private int _dailyLimit = DefaultDailyLimit;
    private DateOnly _requestsDate = DateOnly.FromDateTime(DateTime.UtcNow);
    private DateTime? _dailyLimitExceededUntil; // set on "Limit Exceeded" 429, expires after 1 hour
    private int _consecutiveBurstThrottles; // for exponential backoff on "Too Many Requests"
    private DateTime _lastRequestTime; // for spacing out requests
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
            _dailyLimitExceededUntil = null;
            _consecutiveBurstThrottles = 0;
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
    /// Whether the daily quota has been exhausted (received a "Limit Exceeded" 429).
    /// Burst throttles ("Too Many Requests") do NOT set this - those are transient.
    /// </summary>
    public bool IsRateLimited
    {
        get
        {
            lock (_rateLimitLock)
            {
                return _dailyLimitExceededUntil != null && DateTime.UtcNow < _dailyLimitExceededUntil;
            }
        }
    }

    /// <summary>
    /// Query the CrowdSec CTI Smoke API for an IP's reputation.
    /// Returns the lookup result with an outcome indicating success, not-found, rate-limited, or error.
    /// Spaces out requests by at least 500ms to avoid burst throttling.
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
            return (null, CrowdSecLookupOutcome.QuotaExhausted);
        }

        // Space out requests to avoid burst throttling
        await ThrottleAsync(cancellationToken);

        try
        {
            var client = _httpClientFactory.CreateClient("CrowdSec");
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("x-api-key", apiKey);

            var response = await client.GetAsync($"{BaseUrl}{ipAddress}", cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogDebug("IP {Ip} not found in CrowdSec database", ipAddress);
                OnSuccess();
                return (null, CrowdSecLookupOutcome.NotFound);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return await Handle429Async(response, cancellationToken);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("CrowdSec API key is invalid or expired");
                return (null, CrowdSecLookupOutcome.Error);
            }

            response.EnsureSuccessStatusCode();
            var info = await response.Content.ReadFromJsonAsync<CrowdSecIpInfo>(cancellationToken: cancellationToken);
            OnSuccess();
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
                HttpStatusCode.TooManyRequests => (false, await GetRateLimitMessageAsync(response, cancellationToken)),
                _ => (false, $"Unexpected response: {response.StatusCode}")
            };
        }
        catch (Exception ex)
        {
            return (false, $"Connection error: {ex.Message}");
        }
    }

    private static async Task<string> GetRateLimitMessageAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return body.Contains("Limit Exceeded", StringComparison.OrdinalIgnoreCase)
            ? "Daily quota exhausted - resets at midnight UTC"
            : "Too many requests - try again in a few seconds";
    }

    /// <summary>
    /// Parse the 429 response body to distinguish daily quota exhaustion from burst throttling.
    /// "Limit Exceeded" = daily quota gone. "Too Many Requests" = slow down.
    /// </summary>
    private async Task<(CrowdSecIpInfo? Info, CrowdSecLookupOutcome Outcome)> Handle429Async(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var isDailyLimit = body.Contains("Limit Exceeded", StringComparison.OrdinalIgnoreCase);

        if (isDailyLimit)
        {
            _logger.LogWarning(
                "CrowdSec daily quota exhausted (429 Limit Exceeded) after {RequestsToday}/{DailyLimit} requests today",
                _requestsToday, _dailyLimit);
            lock (_rateLimitLock)
            {
                _dailyLimitExceededUntil = DateTime.UtcNow.AddHours(1);
                _consecutiveBurstThrottles = 0;
            }
            return (null, CrowdSecLookupOutcome.QuotaExhausted);
        }

        // Burst throttle - exponential backoff
        int backoffMs;
        lock (_rateLimitLock)
        {
            _consecutiveBurstThrottles++;
            backoffMs = Math.Min(
                (int)(Math.Pow(2, _consecutiveBurstThrottles) * 500),
                MaxBurstBackoffMs);
            // Undo the request count increment - this request didn't actually consume quota
            if (_requestsToday > 0) _requestsToday--;
        }

        _logger.LogWarning(
            "CrowdSec burst throttle (429 Too Many Requests) after {RequestsToday}/{DailyLimit} requests today. " +
            "Consecutive throttles: {Count}. Next backoff: {BackoffMs}ms. Response: {Body}",
            _requestsToday, _dailyLimit, _consecutiveBurstThrottles, backoffMs, body);

        return (null, CrowdSecLookupOutcome.BurstThrottled);
    }

    /// <summary>
    /// Wait at least MinRequestIntervalMs between API calls, plus any exponential backoff
    /// from prior burst throttles.
    /// </summary>
    private async Task ThrottleAsync(CancellationToken cancellationToken)
    {
        int waitMs;
        lock (_rateLimitLock)
        {
            var elapsed = (int)(DateTime.UtcNow - _lastRequestTime).TotalMilliseconds;

            // Base interval + exponential backoff if we've been throttled
            var targetInterval = MinRequestIntervalMs;
            if (_consecutiveBurstThrottles > 0)
            {
                targetInterval = Math.Min(
                    (int)(Math.Pow(2, _consecutiveBurstThrottles) * 500),
                    MaxBurstBackoffMs);
            }

            waitMs = Math.Max(0, targetInterval - elapsed);
            _lastRequestTime = DateTime.UtcNow.AddMilliseconds(waitMs);
        }

        if (waitMs > 0)
        {
            _logger.LogDebug("CrowdSec throttle: waiting {WaitMs}ms before next request", waitMs);
            await Task.Delay(waitMs, cancellationToken);
        }
    }

    /// <summary>
    /// Reset burst throttle counter on successful response.
    /// </summary>
    private void OnSuccess()
    {
        lock (_rateLimitLock)
        {
            _consecutiveBurstThrottles = 0;
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
                _dailyLimitExceededUntil = null;
            }

            // Only stop if CrowdSec actually returned "Limit Exceeded" recently (within the last hour).
            // We don't know when CrowdSec resets their daily window, so back off for
            // 1 hour and try again rather than blocking the entire day.
            if (_dailyLimitExceededUntil != null && DateTime.UtcNow < _dailyLimitExceededUntil)
                return false;

            _requestsToday++;
            return true;
        }
    }
}
