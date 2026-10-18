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
    /// <summary>IP is RFC1918/loopback/link-local - reputation lookup not applicable.</summary>
    NotApplicable,
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
    private const int MinRequestIntervalMs = 500;
    private const int MaxBurstBackoffMs = 30_000;

    // CrowdSec counts quota and burst throttles per key, so each key has its own state. In memory
    // only: a restart starts every key's count at zero.
    private sealed class KeyState
    {
        public int RequestsToday;
        public DateOnly RequestsDate = DateOnly.FromDateTime(DateTime.UtcNow);
        public DateTime? DailyLimitExceededUntil; // set on "Limit Exceeded" 429, expires after 1 hour
        public int ConsecutiveBurstThrottles; // for exponential backoff on "Too Many Requests"
        public DateTime LastRequestTime; // for spacing out requests
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, KeyState> _states = new();

    public CrowdSecClient(IHttpClientFactory httpClientFactory, ILogger<CrowdSecClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // Keyed by a hash so the key itself is never held in a long-lived dictionary.
    private KeyState StateFor(string apiKey) =>
        _states.GetOrAdd(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(apiKey.Trim())))[..16], _ => new KeyState());

    /// <summary>Lookups made with <paramref name="apiKey"/> today (UTC), for the usage display.</summary>
    public int RequestsToday(string apiKey)
    {
        var s = StateFor(apiKey);
        lock (s)
        {
            return s.RequestsDate == DateOnly.FromDateTime(DateTime.UtcNow) ? s.RequestsToday : 0;
        }
    }

    /// <summary>
    /// Whether <paramref name="apiKey"/>'s daily quota has been exhausted (received a "Limit Exceeded" 429).
    /// Burst throttles ("Too Many Requests") do NOT set this - those are transient.
    /// </summary>
    public bool IsRateLimited(string apiKey)
    {
        var s = StateFor(apiKey);
        lock (s)
        {
            return s.DailyLimitExceededUntil != null && DateTime.UtcNow < s.DailyLimitExceededUntil;
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

        var state = StateFor(apiKey);
        if (!CheckAndIncrementRateLimit(state))
        {
            _logger.LogDebug("CrowdSec daily quota reached ({Requests} lookups today)", state.RequestsToday);
            return (null, CrowdSecLookupOutcome.QuotaExhausted);
        }

        // Space out requests to avoid burst throttling
        await ThrottleAsync(state, cancellationToken);

        try
        {
            var client = _httpClientFactory.CreateClient("CrowdSec");
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("x-api-key", apiKey);

            var response = await client.GetAsync($"{BaseUrl}{ipAddress}", cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogDebug("IP {Ip} not found in CrowdSec database", ipAddress);
                OnSuccess(state);
                return (null, CrowdSecLookupOutcome.NotFound);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return await Handle429Async(state, response, cancellationToken);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("CrowdSec API key is invalid or expired");
                return (null, CrowdSecLookupOutcome.Error);
            }

            response.EnsureSuccessStatusCode();
            var info = await response.Content.ReadFromJsonAsync<CrowdSecIpInfo>(cancellationToken: cancellationToken);
            OnSuccess(state);
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

            // Use loopback IP - guaranteed 404 (no data to serve, may not count against quota)
            var response = await client.GetAsync($"{BaseUrl}127.0.0.1", cancellationToken);

            return response.StatusCode switch
            {
                HttpStatusCode.NotFound => (true, "API key is valid"),
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
        KeyState state, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var isDailyLimit = body.Contains("Limit Exceeded", StringComparison.OrdinalIgnoreCase);

        if (isDailyLimit)
        {
            int requests;
            lock (state)
            {
                state.DailyLimitExceededUntil = DateTime.UtcNow.AddHours(1);
                state.ConsecutiveBurstThrottles = 0;
                requests = state.RequestsToday;
            }
            _logger.LogWarning("CrowdSec daily quota exhausted (429 Limit Exceeded) after {RequestsToday} requests today", requests);
            return (null, CrowdSecLookupOutcome.QuotaExhausted);
        }

        // Burst throttle - exponential backoff
        int backoffMs, requestsToday, throttles;
        lock (state)
        {
            state.ConsecutiveBurstThrottles++;
            backoffMs = Math.Min(
                (int)(Math.Pow(2, state.ConsecutiveBurstThrottles) * 500),
                MaxBurstBackoffMs);
            // Undo the request count increment - this request didn't actually consume quota
            if (state.RequestsToday > 0) state.RequestsToday--;
            requestsToday = state.RequestsToday;
            throttles = state.ConsecutiveBurstThrottles;
        }

        _logger.LogWarning(
            "CrowdSec burst throttle (429 Too Many Requests) after {RequestsToday} requests today. " +
            "Consecutive throttles: {Count}. Next backoff: {BackoffMs}ms. Response: {Body}",
            requestsToday, throttles, backoffMs, body);

        return (null, CrowdSecLookupOutcome.BurstThrottled);
    }

    /// <summary>
    /// Wait at least MinRequestIntervalMs between API calls, plus any exponential backoff
    /// from prior burst throttles.
    /// </summary>
    private async Task ThrottleAsync(KeyState state, CancellationToken cancellationToken)
    {
        int waitMs;
        lock (state)
        {
            var elapsed = (int)(DateTime.UtcNow - state.LastRequestTime).TotalMilliseconds;

            // Base interval + exponential backoff if we've been throttled
            var targetInterval = MinRequestIntervalMs;
            if (state.ConsecutiveBurstThrottles > 0)
            {
                targetInterval = Math.Min(
                    (int)(Math.Pow(2, state.ConsecutiveBurstThrottles) * 500),
                    MaxBurstBackoffMs);
            }

            waitMs = Math.Max(0, targetInterval - elapsed);
            state.LastRequestTime = DateTime.UtcNow.AddMilliseconds(waitMs);
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
    private static void OnSuccess(KeyState state)
    {
        lock (state)
        {
            state.ConsecutiveBurstThrottles = 0;
        }
    }

    private static bool CheckAndIncrementRateLimit(KeyState state)
    {
        lock (state)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (state.RequestsDate != today)
            {
                state.RequestsDate = today;
                state.RequestsToday = 0;
                state.DailyLimitExceededUntil = null;
            }

            // Only stop if CrowdSec actually returned "Limit Exceeded" recently (within the last hour).
            // We don't know when CrowdSec resets their daily window, so back off for
            // 1 hour and try again rather than blocking the entire day.
            if (state.DailyLimitExceededUntil != null && DateTime.UtcNow < state.DailyLimitExceededUntil)
                return false;

            state.RequestsToday++;
            return true;
        }
    }
}
