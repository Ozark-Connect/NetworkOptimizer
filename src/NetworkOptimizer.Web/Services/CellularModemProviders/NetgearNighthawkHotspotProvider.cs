using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;

namespace NetworkOptimizer.Web.Services.CellularModemProviders;

/// <summary>
/// Cellular modem provider for Netgear Nighthawk M-series hotspots
/// (MR5200/sdxprairie, M6/sdxlemur, and similar).
///
/// Uses the NetgearWebApp HTTP/JSON interface at /api/model.json. Read access
/// is gated by a Referer header check; full data (including throughput
/// counters) requires authentication via /Forms/config.
///
/// Sessions are sticky per host - a CookieContainer is kept alive between
/// polls and re-bootstrapped when the server redirects to /sess_cd_tmp
/// (session expired). Auth flow follows the amelchio/eternalegypt Python
/// library, adapted for the M-series internalapi=1 endpoint.
/// </summary>
public sealed class NetgearNighthawkHotspotProvider : ICellularModemProvider, IDisposable
{
    /// <inheritdoc/>
    public string ProviderKey => "netgear-nighthawk-hotspot";

    /// <inheritdoc/>
    public string DisplayName => "Netgear Nighthawk hotspot (HTTP)";

    private const int DefaultTimeoutSeconds = 15;
    private const int MaxBootstrapRedirects = 5;

    private readonly ILogger<NetgearNighthawkHotspotProvider> _logger;
    private readonly ConcurrentDictionary<string, NetgearSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonDocumentOptions _jsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public NetgearNighthawkHotspotProvider(ILogger<NetgearNighthawkHotspotProvider> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<CellularModemStats?> PollAsync(
        ModemPollContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
        {
            _logger.LogWarning("Netgear poll requested for modem {Name} but Host is empty", context.Name);
            return null;
        }

        var hasPassword = !string.IsNullOrEmpty(context.Password);

        try
        {
            // First attempt with current session (or fresh bootstrap if none)
            var json = await FetchModelJsonAsync(context, requireAuth: hasPassword, cancellationToken);
            if (json == null)
            {
                _logger.LogWarning("Netgear poll for {Name} ({Host}) returned no data", context.Name, context.Host);
                return null;
            }

            using var doc = JsonDocument.Parse(json, _jsonOptions);
            return NetgearModelJsonParser.Parse(doc.RootElement, context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling Netgear modem {Name} at {Host}", context.Name, context.Host);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<(bool success, string message)> TestConnectionAsync(
        ModemPollContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
        {
            return (false, "Host is empty");
        }

        try
        {
            var json = await FetchModelJsonAsync(context, requireAuth: false, cancellationToken);
            if (json == null)
            {
                return (false, "Could not reach Netgear hotspot or fetch model.json");
            }

            using var doc = JsonDocument.Parse(json, _jsonOptions);
            var deviceName = TryGetString(doc.RootElement, "general", "deviceName") ?? "unknown";
            var platform = TryGetString(doc.RootElement, "general", "platform") ?? "unknown";
            return (true, $"Detected {deviceName} (platform: {platform})");
        }
        catch (Exception ex)
        {
            return (false, $"Probe failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetch the model.json document, bootstrapping or logging in as needed.
    /// Returns the JSON text on success, null on failure.
    /// </summary>
    private async Task<string?> FetchModelJsonAsync(
        ModemPollContext context,
        bool requireAuth,
        CancellationToken cancellationToken)
    {
        // First attempt
        var session = await GetOrCreateSessionAsync(context, requireAuth, cancellationToken);
        if (session == null) return null;

        var attempt = await TryFetchAsync(session, context.Host, cancellationToken);
        if (attempt.success) return attempt.body;

        // 302 means session expired or never authenticated.
        // Recreate session and try once more.
        if (attempt.sessionExpired)
        {
            _logger.LogInformation(
                "Netgear session for {Host} expired (302 to /sess_cd_tmp), rebuilding",
                context.Host);
            InvalidateSession(context.Host);

            session = await GetOrCreateSessionAsync(context, requireAuth, cancellationToken);
            if (session == null) return null;

            attempt = await TryFetchAsync(session, context.Host, cancellationToken);
            if (attempt.success) return attempt.body;
        }

        return null;
    }

    /// <summary>
    /// One GET to /api/model.json. Returns (success, body, sessionExpired).
    /// </summary>
    private async Task<(bool success, string? body, bool sessionExpired)> TryFetchAsync(
        NetgearSession session,
        string host,
        CancellationToken cancellationToken)
    {
        var url = $"http://{host}/api/model.json?internalapi=1&x={Random.Shared.Next(1, 100_000)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri($"http://{host}/index.html");

        using var response = await session.Client.SendAsync(
            request, HttpCompletionOption.ResponseContentRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            // Defensive: a 200 from /index.html (no JSON) means session expired
            // but the server returned the login page instead of redirecting.
            if (body.StartsWith("<", StringComparison.Ordinal))
            {
                return (false, null, sessionExpired: true);
            }
            return (true, body, sessionExpired: false);
        }

        if (response.StatusCode == HttpStatusCode.Redirect ||
            response.StatusCode == HttpStatusCode.Found)
        {
            var location = response.Headers.Location?.ToString() ?? "";
            // Redirect to /sess_cd_tmp = unauthenticated/expired
            var sessionExpired = location.Contains("sess_cd_tmp", StringComparison.OrdinalIgnoreCase);
            return (false, null, sessionExpired);
        }

        _logger.LogWarning(
            "Unexpected status {Status} from Netgear model.json at {Host}",
            response.StatusCode, host);
        return (false, null, sessionExpired: false);
    }

    /// <summary>
    /// Get an existing session for the host, or create one by running the
    /// bootstrap (and login, if requireAuth is true and a password is set).
    /// </summary>
    private async Task<NetgearSession?> GetOrCreateSessionAsync(
        ModemPollContext context,
        bool requireAuth,
        CancellationToken cancellationToken)
    {
        // Fast path: existing session that's still authenticated to the right level
        if (_sessions.TryGetValue(context.Host, out var existing))
        {
            if (!requireAuth || existing.IsAuthenticated)
            {
                return existing;
            }
            // We need auth but have an unauthenticated session - tear it down
            InvalidateSession(context.Host);
        }

        var session = CreateSession(context.Host);

        var bootstrapped = await BootstrapAsync(session, context.Host, cancellationToken);
        if (!bootstrapped)
        {
            session.Dispose();
            return null;
        }

        if (requireAuth && !string.IsNullOrEmpty(context.Password))
        {
            var loggedIn = await LoginAsync(session, context.Host, context.Password!, cancellationToken);
            if (!loggedIn)
            {
                _logger.LogWarning("Netgear login failed for {Host}", context.Host);
                // Keep the anonymous session - polling can still return signal data
            }
            else
            {
                session.IsAuthenticated = true;
            }
        }

        _sessions[context.Host] = session;
        return session;
    }

    /// <summary>
    /// GET / and follow redirects through /sess_cd_tmp to land on /index.html
    /// with a real sessionId cookie set. Cookie persistence is automatic via
    /// the session's CookieContainer.
    /// </summary>
    private async Task<bool> BootstrapAsync(
        NetgearSession session,
        string host,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{host}/");
        try
        {
            using var response = await session.Client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            // We don't care about the body - we care that the redirect chain
            // landed and cookies were set. Auto-redirect handles the chain.
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bootstrap to {Host} failed", host);
            return false;
        }
    }

    /// <summary>
    /// Fetch the pre-login model.json to extract session.secToken, then POST
    /// session.password + token to /Forms/config. Returns true on HTTP 204
    /// (the eternalegypt success path) or 302 (server returned ok_redirect).
    /// </summary>
    private async Task<bool> LoginAsync(
        NetgearSession session,
        string host,
        string password,
        CancellationToken cancellationToken)
    {
        // Step 1: fetch model.json with Referer to extract secToken
        var (ok, body, _) = await TryFetchAsync(session, host, cancellationToken);
        if (!ok || string.IsNullOrEmpty(body))
        {
            _logger.LogWarning("Could not fetch pre-login model.json from {Host} for token extraction", host);
            return false;
        }

        string? secToken;
        try
        {
            using var doc = JsonDocument.Parse(body, _jsonOptions);
            secToken = TryGetString(doc.RootElement, "session", "secToken");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Pre-login model.json from {Host} was not valid JSON", host);
            return false;
        }

        if (string.IsNullOrEmpty(secToken))
        {
            _logger.LogWarning("session.secToken not present in model.json from {Host}", host);
            return false;
        }

        // Step 2: POST credentials
        var loginUrl = $"http://{host}/Forms/config";
        var formFields = new Dictionary<string, string>
        {
            ["session.password"] = password,
            ["token"] = secToken,
        };

        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, loginUrl)
        {
            Content = new FormUrlEncodedContent(formFields),
        };
        loginRequest.Headers.Referrer = new Uri($"http://{host}/index.html");

        try
        {
            using var response = await session.Client.SendAsync(
                loginRequest, HttpCompletionOption.ResponseContentRead, cancellationToken);

            // Empirically: 204 No Content on success (no ok_redirect set),
            // 302 if ok_redirect was provided, 400 if rejected.
            if (response.StatusCode == HttpStatusCode.NoContent ||
                response.StatusCode == HttpStatusCode.Redirect ||
                response.StatusCode == HttpStatusCode.Found ||
                response.StatusCode == HttpStatusCode.OK)
            {
                return true;
            }

            _logger.LogWarning("Netgear login to {Host} returned {Status}", host, response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Netgear login POST to {Host} failed", host);
            return false;
        }
    }

    private NetgearSession CreateSession(string host)
    {
        var cookies = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            AllowAutoRedirect = false,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        // The bootstrap call needs to follow redirects through /sess_cd_tmp
        // to mint a real session cookie. We toggle AllowAutoRedirect via two
        // clients so that data requests can detect 302 explicitly.
        var client = new HttpClient(new RedirectAwareHandler(handler), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds),
        };

        return new NetgearSession
        {
            Host = host,
            CookieContainer = cookies,
            Handler = handler,
            Client = client,
            IsAuthenticated = false,
        };
    }

    private void InvalidateSession(string host)
    {
        if (_sessions.TryRemove(host, out var session))
        {
            session.Dispose();
        }
    }


    // Generic helpers for path-based JSON access

    private static string? TryGetString(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object) return null;
            if (!current.TryGetProperty(segment, out var next)) return null;
            current = next;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }


    public void Dispose()
    {
        foreach (var entry in _sessions.Values)
        {
            entry.Dispose();
        }
        _sessions.Clear();
    }

    /// <summary>
    /// Per-host session state. Disposed when invalidated or when the
    /// provider itself is disposed.
    /// </summary>
    private sealed class NetgearSession : IDisposable
    {
        public required string Host { get; init; }
        public required CookieContainer CookieContainer { get; init; }
        public required HttpClientHandler Handler { get; init; }
        public required HttpClient Client { get; init; }
        public bool IsAuthenticated { get; set; }

        public void Dispose()
        {
            try { Client.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Forwarding handler that explicitly follows redirects ONLY when the
    /// request is the bootstrap GET /. Other requests (model.json,
    /// /Forms/config) must see the 302 directly so the caller can detect
    /// session expiry.
    /// </summary>
    private sealed class RedirectAwareHandler : DelegatingHandler
    {
        public RedirectAwareHandler(HttpClientHandler inner) : base(inner) { }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var isBootstrap = request.Method == HttpMethod.Get &&
                              (request.RequestUri?.AbsolutePath == "/" ||
                               request.RequestUri?.AbsolutePath?.Equals("/index.html", StringComparison.OrdinalIgnoreCase) == true);

            if (!isBootstrap)
            {
                return await base.SendAsync(request, cancellationToken);
            }

            // Manual redirect chain for bootstrap, capped to avoid loops
            var current = request;
            HttpResponseMessage? response = null;
            for (int hop = 0; hop <= MaxBootstrapRedirects; hop++)
            {
                response = await base.SendAsync(current, cancellationToken);
                if (response.StatusCode != HttpStatusCode.Redirect &&
                    response.StatusCode != HttpStatusCode.Found &&
                    response.StatusCode != HttpStatusCode.MovedPermanently &&
                    response.StatusCode != HttpStatusCode.SeeOther &&
                    response.StatusCode != HttpStatusCode.TemporaryRedirect)
                {
                    return response;
                }

                var location = response.Headers.Location;
                if (location == null)
                {
                    return response;
                }

                // Build absolute URL if redirect was relative
                var nextUri = location.IsAbsoluteUri
                    ? location
                    : new Uri(current.RequestUri!, location);

                response.Dispose();
                current = new HttpRequestMessage(HttpMethod.Get, nextUri);
            }

            // Hit max redirects
            return response ?? new HttpResponseMessage(HttpStatusCode.LoopDetected);
        }
    }
}
