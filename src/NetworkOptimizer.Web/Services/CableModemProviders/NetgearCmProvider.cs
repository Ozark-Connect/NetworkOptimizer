using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;

namespace NetworkOptimizer.Web.Services.CableModemProviders;

/// <summary>
/// Cable modem provider for Netgear DOCSIS modems. Scrapes the DocsisStatus status page and
/// parses downstream/upstream channel tables. One provider auto-detects across the family so a
/// user never has to know which page/auth their specific model uses. Confirmed against the
/// CM600, CM1000 (.asp) and CM700, CM2050V (.htm); other models are reached by the same
/// fallback without being individually verified.
///
/// Netgear is inconsistent about the page extension: some models serve
/// <c>DocsisStatus.asp</c> (e.g. CM600, CM1000) while others serve <c>DocsisStatus.htm</c>
/// (e.g. CM700, CM2050V) and return 404 for the .asp path. Model number does not reliably
/// predict which (CM600 and CM700 are both DOCSIS 3.0 yet differ), so the provider tries
/// the configured/default path and transparently falls back to the alternate extension.
///
/// Auth also varies. Most models gate the page behind HTTP Basic auth (the CM700, server
/// "NET-DK/1.0", additionally requires an anti-CSRF cookie it sets on the initial 401 to be
/// echoed back - the first request primes the cookie jar and FetchPageAsync retries once to
/// satisfy it). The CM2050V's .htm UI instead uses a form login: a GET to the root seeds an
/// <c>XSRF_TOKEN</c> cookie, credentials are POSTed to <c>/goform/Login</c>, and the session is
/// bound to the source IP. The fallback matrix tries Basic first (covers most models in one
/// request) and form login last, remembering whichever works per config.
///
/// The pages also differ in how channel data is delivered: the .asp page renders the
/// downstream/upstream tables server-side, while the .htm page ships empty tables and a
/// JavaScript tagValueList that the browser expands client-side. The parser handles both - the
/// server-rendered tables first, then the tagValueList for any table left empty. The CM2050V's
/// .htm page carries four such tables: SC-QAM + OFDM downstream and ATDMA + OFDMA upstream, each
/// in its own Init function; all four are parsed.
/// </summary>
public sealed class NetgearCmProvider : ICableModemProvider
{
    /// <inheritdoc/>
    public string ProviderKey => "netgear";

    /// <inheritdoc/>
    public string DisplayName => "Netgear / Nighthawk CM (HTTP)";

    private const string DefaultStatusPath = "/DocsisStatus.asp";
    private const string LoginPath = "/goform/Login";
    private const int TimeoutSeconds = 15;
    private const int MaxRetries = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    // Cap on the bytes read from the raw-socket fallback (the DOCSIS pages are tens of KB); guards
    // against a modem that never closes the connection.
    private const int MaxRawResponseBytes = 4 * 1024 * 1024;

    // Some Netgear modems (e.g. CM600) allow only one web session at a time. When another
    // session is active, any page 302-redirects to MultiLogin.asp, which offers to log out
    // the existing session. These patterns pull the takeover token and RetailSessionId from
    // that page so polling can claim the session instead of failing.
    private static readonly Regex MultiLoginSessionRx =
        new(@"goform/MultiLogin\?session=([0-9A-Za-z]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RetailSessionRx =
        new("name=[\"']?RetailSessionId[\"']?\\s+value=[\"']?([0-9A-Za-z]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // CM700 (and similar) serve DocsisStatus.htm with empty channel tables that the browser
    // fills client-side from a JavaScript tagValueList: a leading channel count followed by
    // pipe-delimited per-channel fields. Capture the live single-quoted assignment for each
    // table (the placeholder/example assignment in the function is double-quoted and inside a
    // /* */ comment, so the single-quote match skips it).
    private static readonly Regex DsTagValueListRx =
        new(@"InitDsTableTagValue\s*\(\s*\)\s*\{.*?var\s+tagValueList\s*=\s*'([^']*)'",
            RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex UsTagValueListRx =
        new(@"InitUsTableTagValue\s*\(\s*\)\s*\{.*?var\s+tagValueList\s*=\s*'([^']*)'",
            RegexOptions.Singleline | RegexOptions.Compiled);
    // The CM2050V's .htm page carries two extra tables for the OFDM downstream and OFDMA
    // upstream channels, each in its own Init function. Anchoring on the distinct function
    // names keeps these separate from the SC-QAM/ATDMA tables above.
    private static readonly Regex DsOfdmTagValueListRx =
        new(@"InitDsOfdmTableTagValue\s*\(\s*\)\s*\{.*?var\s+tagValueList\s*=\s*'([^']*)'",
            RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex UsOfdmaTagValueListRx =
        new(@"InitUsOfdmaTableTagValue\s*\(\s*\)\s*\{.*?var\s+tagValueList\s*=\s*'([^']*)'",
            RegexOptions.Singleline | RegexOptions.Compiled);

    // The form-login page (.htm DOCSIS 3.1 models) bakes a per-load cache-buster id into its
    // form action (e.g. action="/goform/Login?id=1248795675"); we reuse it when present.
    private static readonly Regex LoginIdRx =
        new(@"goform/Login\?id=(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // NET-DK firmware (e.g. CM700) sets the anti-CSRF cookie on its initial 401. The raw-socket
    // fallback pulls it straight from the response bytes so a corrupted Set-Cookie header name
    // (the same byte-drop corruption that forces the raw path) can't hide it.
    private static readonly Regex XsrfCookieRx =
        new(@"XSRF_TOKEN=([^;\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ILogger<NetgearCmProvider> _logger;

    /// <summary>
    /// Remembers the (status-page path, form-login) combination that last succeeded per
    /// CmConfiguration.Id so later polls go straight to it instead of re-probing the dead
    /// extension or wrong auth style every interval.
    /// </summary>
    private readonly ConcurrentDictionary<int, (string Path, bool FormLogin)> _attemptCache = new();

    public NetgearCmProvider(ILogger<NetgearCmProvider> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<CableModemStats?> PollAsync(
        CmPollContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
        {
            _logger.LogWarning("Netgear CM poll requested but Host is empty (config {Id})", context.Id);
            return null;
        }

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                // Force the full path/credential matrix only on the final retry, so a cached
                // combo's transient blip recovers via the earlier retries without probing
                // (and logging) every other combo.
                var html = await FetchWithFallbackAsync(context, cancellationToken, fullMatrix: attempt == MaxRetries);
                if (html == null)
                {
                    _logger.LogWarning(
                        "Netgear CM at {Host} returned empty response (attempt {Attempt}/{Max})",
                        context.Host, attempt, MaxRetries);
                    if (attempt < MaxRetries)
                    {
                        await Task.Delay(RetryDelay, cancellationToken);
                        continue;
                    }
                    return null;
                }

                var stats = ParseDocsisStatus(html, context);
                _logger.LogDebug(
                    "Netgear CM {Name} polled: {DsCount} DS channels, {UsCount} US channels",
                    context.Name, stats.DownstreamChannels.Count, stats.UpstreamChannels.Count);
                return stats;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries)
            {
                _logger.LogDebug(
                    ex, "Transient error polling Netgear CM {Name} (attempt {Attempt}/{Max})",
                    context.Name, attempt, MaxRetries);
                await Task.Delay(RetryDelay, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error polling Netgear CM {Name} at {Host}", context.Name, context.Host);
                return null;
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public async Task<(bool Success, string Message)> TestConnectionAsync(
        CmPollContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
            return (false, "Host is empty");

        try
        {
            // A manual test should probe everything to find a working combo, so force the
            // full matrix regardless of any cached result.
            var html = await FetchWithFallbackAsync(context, cancellationToken, fullMatrix: true);
            if (html == null)
                return (false, "No response from cable modem");

            // Parse through the full pipeline so both the server-rendered table format
            // (.asp) and the JavaScript tagValueList format (.htm, e.g. CM700) are counted.
            var stats = ParseDocsisStatus(html, context);
            var dsCount = stats.DownstreamChannels.Count;
            var usCount = stats.UpstreamChannels.Count;

            if (dsCount == 0 && usCount == 0)
                return (false, "Connected but no DOCSIS channels found. Check the status page path.");

            return (true, $"Connected - {dsCount} downstream, {usCount} upstream channels detected");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return (false, "Authentication failed - check username/password");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    private static string BuildUrl(CmPollContext context, string path)
    {
        var port = context.Port > 0 ? context.Port : 80;
        var portSuffix = port == 80 ? "" : $":{port}";

        return $"http://{context.Host}{portSuffix}{path}";
    }

    /// <summary>
    /// Fetch the DOCSIS status page, transparently falling back across both Netgear page
    /// extensions (.asp &lt;-&gt; .htm) AND auth styles (HTTP Basic, then a form login - POST to
    /// /goform/Login - for models that bind the web session to the source IP, confirmed on the
    /// CM2050V). Model number doesn't predict which combo a modem needs. A page is accepted only
    /// when it actually looks like a status
    /// page; a wrong combo that still returns HTTP 200 (e.g. a login/index page) advances to the
    /// next attempt, as does any HTTP/transport error. The last attempt's error propagates, so a
    /// genuine auth failure still surfaces. The winning combo is cached per config so later polls
    /// go straight to it.
    /// </summary>
    private async Task<string?> FetchWithFallbackAsync(
        CmPollContext context,
        CancellationToken cancellationToken,
        bool fullMatrix)
    {
        var attempts = ResolveAttempts(context, fullMatrix);
        var hasCreds = !string.IsNullOrEmpty(context.Username) && !string.IsNullOrEmpty(context.Password);

        for (int i = 0; i < attempts.Count; i++)
        {
            var (path, formLogin) = attempts[i];
            var url = BuildUrl(context, path);
            var isLast = i == attempts.Count - 1;
            try
            {
                var html = formLogin
                    ? await FetchViaFormLoginAsync(url, context.Username, context.Password, cancellationToken)
                    : await FetchPageAsync(url, hasCreds ? context.Username : null, hasCreds ? context.Password : null, cancellationToken);

                // Accept only a real DocsisStatus page. A wrong extension or auth style can still
                // return HTTP 200 with a login/index page (notably a Basic GET against a
                // form-login model), which must not be cached as the winning combo.
                if (IsStatusPage(html))
                {
                    if (context.Id > 0)
                        _attemptCache[context.Id] = (path, formLogin);
                    return html;
                }

                if (isLast)
                    return html;

                _logger.LogDebug(
                    "Netgear CM {Name}: {Path} (form={Form}) returned a non-status page; trying next attempt",
                    context.Name, path, formLogin);
            }
            catch (HttpRequestException ex) when (!isLast)
            {
                // Advance on any HTTP status error (404/403/5xx) or transport error (connection
                // reset/closed, where StatusCode is null). Netgear firmware signals an
                // unavailable path inconsistently - the wrong extension 404s on some models and
                // closes the connection on others - so we don't gate on a specific code.
                _logger.LogDebug(
                    "Netgear CM {Name}: {Path} (form={Form}) failed ({Reason}); trying next attempt",
                    context.Name, path, formLogin,
                    ex.StatusCode is { } status ? $"HTTP {(int)status}" : ex.Message);
            }
        }

        return null;
    }

    /// <summary>
    /// Build the ordered list of (path, form-login) attempts: the configured/default path and the
    /// alternate Netgear extension (.asp ↔ .htm) tried with HTTP Basic first (one request, covers
    /// most models), then a form-login attempt on the .htm page for the newer models that need
    /// it. Form login requires credentials to POST, so it's only added when the config has them.
    /// The combo recorded as working for this config is moved to the front.
    /// </summary>
    private List<(string Path, bool FormLogin)> ResolveAttempts(CmPollContext context, bool fullMatrix)
    {
        var hasCreds = !string.IsNullOrEmpty(context.Username) && !string.IsNullOrEmpty(context.Password);

        // Fast path: a known-good combo is cached and we're not forcing a re-probe. Return only
        // that combo so a transient blip self-recovers on the next poll retry instead of probing
        // (and logging a 404 or connection error for) the wrong extension/auth. The poll loop
        // forces the full matrix on its final retry, so a combo that has genuinely stopped working
        // is still re-discovered.
        if (!fullMatrix && context.Id > 0 && _attemptCache.TryGetValue(context.Id, out var cached))
            return new List<(string Path, bool FormLogin)> { cached };

        var configured = string.IsNullOrWhiteSpace(context.StatusPagePath)
            ? DefaultStatusPath
            : context.StatusPagePath;

        var paths = new List<string> { configured };
        var alternate = AlternateNetgearPath(configured);
        if (alternate != null)
            paths.Add(alternate);

        // Basic-auth attempts first (the CM700 also wants an anti-CSRF cookie, primed in
        // FetchPageAsync). Then a single form-login attempt on the .htm page for models that gate
        // it behind a /goform/Login session (confirmed on the CM2050V). Form login needs
        // credentials to POST, so it's only added when the config has them.
        var attempts = paths.Select(p => (Path: p, FormLogin: false)).ToList();
        if (hasCreds)
        {
            var htmPath = paths.FirstOrDefault(p => p.EndsWith("DocsisStatus.htm", StringComparison.OrdinalIgnoreCase));
            if (htmPath != null)
                attempts.Add((htmPath, true));
        }

        if (context.Id > 0 && _attemptCache.TryGetValue(context.Id, out var lastGood))
        {
            var idx = attempts.FindIndex(a => a.Path == lastGood.Path && a.FormLogin == lastGood.FormLogin);
            if (idx > 0)
            {
                var item = attempts[idx];
                attempts.RemoveAt(idx);
                attempts.Insert(0, item);
            }
        }

        return attempts;
    }

    /// <summary>
    /// Returns the other Netgear DOCSIS status-page extension for a given path, or null if
    /// the path is not a recognized DocsisStatus page. CM600/CM1000 use <c>.asp</c>; CM700
    /// uses <c>.htm</c>, and model number does not reliably predict which.
    /// </summary>
    private static string? AlternateNetgearPath(string path)
    {
        if (path.EndsWith("DocsisStatus.asp", StringComparison.OrdinalIgnoreCase))
            return string.Concat(path.AsSpan(0, path.Length - 4), ".htm");
        if (path.EndsWith("DocsisStatus.htm", StringComparison.OrdinalIgnoreCase))
            return string.Concat(path.AsSpan(0, path.Length - 4), ".asp");
        return null;
    }

    /// <summary>
    /// True when a fetched page is a DocsisStatus page - either the server-rendered ds/us tables
    /// (.asp) or the client-side tagValueList Init functions (.htm) - rather than a login/index
    /// page returned when the wrong extension or auth style is tried. Both forms contain the
    /// "dsTable"/"usTable" token (as an element id, or inside InitDsTableTagValue/InitUsTableTagValue),
    /// which a login page does not.
    /// </summary>
    private static bool IsStatusPage(string? html) =>
        !string.IsNullOrEmpty(html)
        && (html.Contains("dsTable", StringComparison.OrdinalIgnoreCase)
            || html.Contains("usTable", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Fetch the status page via the form login observed on the CM2050V's .htm UI: GET the root
    /// to seed the <c>XSRF_TOKEN</c> cookie and reuse the form action's cache-buster id, POST the
    /// credentials to <c>/goform/Login</c>, then GET the status page on the same cookie jar. The
    /// modem binds the session to the source IP, so a single HttpClient/CookieContainer carries
    /// it across the three requests.
    /// </summary>
    private async Task<string?> FetchViaFormLoginAsync(
        string statusUrl,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        var baseUrl = new Uri(statusUrl).GetLeftPart(UriPartial.Authority);

        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            CookieContainer = new System.Net.CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = true,
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
        };

        // Prime the XSRF_TOKEN cookie and reuse the form's cache-buster id. Non-fatal on failure -
        // we fall back to a generated id and rely on the cookie the POST itself would set.
        string? loginId = null;
        try
        {
            using var loginPage = await client.GetAsync($"{baseUrl}/", cancellationToken);
            if (loginPage.IsSuccessStatusCode)
            {
                var loginHtml = await loginPage.Content.ReadAsStringAsync(cancellationToken);
                var idMatch = LoginIdRx.Match(loginHtml);
                if (idMatch.Success)
                    loginId = idMatch.Groups[1].Value;
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Netgear CM form login: could not pre-fetch login page at {BaseUrl}", baseUrl);
        }

        loginId ??= Random.Shared.Next(100_000_000, 999_999_999).ToString();

        var loginContent = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("loginName", username ?? "admin"),
            new KeyValuePair<string, string>("loginPassword", password ?? ""),
        });

        using (var loginResponse = await client.PostAsync($"{baseUrl}{LoginPath}?id={loginId}", loginContent, cancellationToken))
        {
            // Surface transport/HTTP errors (4xx/5xx) so the caller advances to the next combo.
            // Wrong credentials usually 200 back to the login page rather than erroring, so that
            // case isn't decided here - the status GET below plus the caller's IsStatusPage check
            // reject it.
            loginResponse.EnsureSuccessStatusCode();
        }

        using var response = await client.GetAsync(statusUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(content) ? null : content;
    }

    /// <summary>
    /// Fetch a page, falling back to a lenient raw-socket reader when the modem returns a
    /// structurally malformed HTTP response that .NET's strict parser rejects. The NET-DK/1.0
    /// server on some Netgear modems (e.g. the CM700) intermittently drops the leading bytes of
    /// response header lines - "Content-Length" arrives as "ntent-Length", or a stray line loses
    /// its colon - which makes the HttpClient stack throw "Received an invalid header line" even
    /// though browsers parse the same bytes fine. There is no in-framework switch to relax that
    /// parsing (the old .NET Framework useUnsafeHeaderParsing has no modern HttpClient/
    /// SocketsHttpHandler equivalent - see dotnet/runtime#29927), so on exactly that failure we
    /// re-fetch over a TcpClient and parse the headers leniently. Modems that return RFC-compliant
    /// HTTP never throw it, so they never enter the raw path and are completely unaffected.
    /// </summary>
    private async Task<string?> FetchPageAsync(
        string url,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        try
        {
            return await FetchViaHttpClientAsync(url, username, password, cancellationToken);
        }
        catch (HttpRequestException ex) when (IsMalformedHttpResponse(ex))
        {
            _logger.LogDebug(
                ex, "Netgear CM {Url}: HttpClient rejected a malformed HTTP response; retrying via lenient raw-socket reader",
                url);
            return await FetchViaRawSocketAsync(url, username, password, cancellationToken);
        }
    }

    /// <summary>
    /// True when an HttpRequestException reflects a structurally malformed response that .NET
    /// could not parse into an HTTP status, rather than a normal HTTP error status or a connection
    /// failure. Gated on a null StatusCode so a real 401/404/5xx is never diverted to the raw
    /// reader, and matched on the specific parser messages so an offline modem (connection
    /// refused/timed out) isn't either.
    /// </summary>
    private static bool IsMalformedHttpResponse(HttpRequestException ex)
    {
        if (ex.StatusCode is not null)
            return false;

        return Mentions(ex.Message) || (ex.InnerException is { } inner && Mentions(inner.Message));

        static bool Mentions(string m) =>
            m.Contains("invalid header", StringComparison.OrdinalIgnoreCase)
            || m.Contains("header line", StringComparison.OrdinalIgnoreCase)
            || m.Contains("invalid status", StringComparison.OrdinalIgnoreCase)
            || m.Contains("malformed", StringComparison.OrdinalIgnoreCase)
            || m.Contains("response ended prematurely", StringComparison.OrdinalIgnoreCase)
            || m.Contains("invalid chunk", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> FetchViaHttpClientAsync(
        string url,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        // Follow redirects manually (AllowAutoRedirect = false) so the Basic auth header is
        // sent on EVERY hop. .NET drops the Authorization header when auto-following a
        // redirect, which breaks single-session modems (e.g. CM600) that 302 every page to
        // MultiLogin.asp when another session is active - the redirected request would 401.
        // The cookie jar keeps the session across the takeover (GET -> POST -> re-GET).
        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            CookieContainer = new System.Net.CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = false,
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
        };

        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        var response = await GetFollowingRedirectsAsync(client, url, cancellationToken);

        // Some Netgear modems (e.g. CM700, server "NET-DK/1.0") gate the status page behind HTTP
        // Basic auth AND an anti-CSRF cookie: the first request is answered with 401 plus
        // Set-Cookie: XSRF_TOKEN=..., and the Basic credentials are only accepted once that
        // cookie is echoed back. The first request primes the cookie jar (the CookieContainer
        // stores the cookie even on a 401), so retry once - the replayed XSRF_TOKEN now rides
        // along with the preemptive Basic header and the modem returns 200. Modems that don't
        // need this (e.g. CM600, which 200s/302s on the first request) never enter the retry.
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            && !string.IsNullOrEmpty(username)
            && handler.CookieContainer.GetCookies(new Uri(url)).Count > 0)
        {
            response.Dispose();
            response = await GetFollowingRedirectsAsync(client, url, cancellationToken);
        }

        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        // If we landed on the single-session takeover page (rather than the status page),
        // claim the session and re-fetch. Modems without this behavior never hit this path,
        // so the normal flow is unchanged.
        if (IsMultiLoginPage(response.RequestMessage?.RequestUri, content))
        {
            if (await TryTakeOverSessionAsync(client, url, content, cancellationToken))
            {
                response = await GetFollowingRedirectsAsync(client, url, cancellationToken);
                response.EnsureSuccessStatusCode();
                content = await response.Content.ReadAsStringAsync(cancellationToken);
            }
        }

        return string.IsNullOrWhiteSpace(content) ? null : content;
    }

    /// <summary>
    /// GET a URL, following same-style redirects manually so the client's default headers
    /// (notably Basic auth) are re-sent on each hop - unlike HttpClient auto-redirect, which
    /// strips the Authorization header. Resolves relative Location values (e.g.
    /// "MultiLogin.asp"). Stops after a few hops to avoid loops.
    /// </summary>
    private static async Task<HttpResponseMessage> GetFollowingRedirectsAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        var current = url;
        var response = await client.GetAsync(current, cancellationToken);

        for (int hop = 0; hop < 5
            && (int)response.StatusCode is >= 300 and < 400
            && response.Headers.Location != null; hop++)
        {
            var next = new Uri(new Uri(current), response.Headers.Location);
            response.Dispose();
            current = next.ToString();
            response = await client.GetAsync(current, cancellationToken);
        }

        return response;
    }

    /// <summary>
    /// True when a response is the Netgear single-session "MultiLogin" takeover page
    /// (another web session is active) rather than the requested status page.
    /// </summary>
    private static bool IsMultiLoginPage(Uri? finalUri, string content)
    {
        if (finalUri != null && finalUri.AbsolutePath.Contains("MultiLogin", StringComparison.OrdinalIgnoreCase))
            return true;
        return content.Contains("goform/MultiLogin", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Logs out the existing web session by POSTing the MultiLogin takeover form
    /// (<c>yes=yes&amp;Act=yes&amp;RetailSessionId=...</c> to
    /// <c>/goform/MultiLogin?session=...</c>). Returns true if the takeover was submitted.
    /// </summary>
    private async Task<bool> TryTakeOverSessionAsync(
        HttpClient client,
        string statusUrl,
        string multiLoginHtml,
        CancellationToken cancellationToken)
    {
        var sessionMatch = MultiLoginSessionRx.Match(multiLoginHtml);
        var retailMatch = RetailSessionRx.Match(multiLoginHtml);
        if (!sessionMatch.Success || !retailMatch.Success)
        {
            _logger.LogDebug("Netgear CM MultiLogin page seen but takeover tokens not found; cannot reclaim session");
            return false;
        }

        var authority = new Uri(statusUrl).GetLeftPart(UriPartial.Authority);
        var postUrl = $"{authority}/goform/MultiLogin?session={sessionMatch.Groups[1].Value}";
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("yes", "yes"),
            new KeyValuePair<string, string>("Act", "yes"),
            new KeyValuePair<string, string>("RetailSessionId", retailMatch.Groups[1].Value),
        });

        _logger.LogDebug("Netgear CM: another web session active; logging it out via MultiLogin to poll");
        var postResponse = await client.PostAsync(postUrl, form, cancellationToken);
        // The takeover POST returns a 302 redirect on success (we don't auto-follow it; the
        // caller re-fetches the status page). Treat any non-error status as accepted.
        return (int)postResponse.StatusCode < 400;
    }

    /// <summary>
    /// Fetch the status page over a raw <see cref="System.Net.Sockets.TcpClient"/> with a tolerant
    /// reader. Used only as a fallback when HttpClient rejects the modem's malformed HTTP (see
    /// <see cref="FetchPageAsync"/>). Replicates the NET-DK two-step HTTP Basic + anti-CSRF cookie
    /// handshake: the first GET primes the XSRF_TOKEN cookie the modem sets on its 401, and the
    /// second GET replays it alongside the preemptive Basic header. HTTP/1.0 with Connection: close
    /// keeps the response simple (no chunking, no keep-alive); the leniently parsed body is handed
    /// back to the normal parse pipeline. This path covers the Basic + XSRF modems (the only family
    /// observed to corrupt its HTTP); it does not re-implement the CM600 MultiLogin takeover, which
    /// runs on a different server that returns compliant HTTP and so never reaches here.
    /// </summary>
    internal async Task<string?> FetchViaRawSocketAsync(
        string url,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(url);
        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : 80;
        var pathAndQuery = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;

        string? authHeader = null;
        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            authHeader = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        var ct = timeoutCts.Token;

        try
        {
            // First request primes the XSRF_TOKEN cookie the NET-DK modem sets alongside its 401.
            var firstRaw = await RawHttpGetAsync(host, port, pathAndQuery, authHeader, cookie: null, ct);
            var first = ParseRawHttpResponse(firstRaw, firstRaw.Length);

            // Pull the cookie straight from the response header bytes so a corrupted Set-Cookie
            // header name can't hide it. Scope the search to the header region (not the body) so a
            // stray XSRF_TOKEN= occurrence in page content can't be mistaken for the real cookie.
            var (firstHeaderEnd, _) = FindHeaderBodySplit(firstRaw, firstRaw.Length);
            var cookieMatch = XsrfCookieRx.Match(Encoding.Latin1.GetString(firstRaw, 0, firstHeaderEnd));
            var cookie = cookieMatch.Success ? "XSRF_TOKEN=" + cookieMatch.Groups[1].Value : null;

            // Replay the primed cookie unless the first request already returned the page. Gating on
            // "not 200" rather than "exactly 401" means a 401 whose status line was itself corrupted
            // (so it parsed as an unknown status) still gets the cookie retry instead of being
            // dropped - the presence of the anti-CSRF cookie is the real signal the modem wants it.
            var resp = first;
            if (cookie != null && first.StatusCode != 200)
            {
                var secondRaw = await RawHttpGetAsync(host, port, pathAndQuery, authHeader, cookie, ct);
                resp = ParseRawHttpResponse(secondRaw, secondRaw.Length);
            }

            if (resp.StatusCode >= 400)
            {
                throw new HttpRequestException(
                    $"Netgear CM raw fetch of {pathAndQuery} returned HTTP {resp.StatusCode}",
                    inner: null,
                    statusCode: Enum.IsDefined(typeof(System.Net.HttpStatusCode), resp.StatusCode)
                        ? (System.Net.HttpStatusCode)resp.StatusCode
                        : null);
            }

            return string.IsNullOrWhiteSpace(resp.Body) ? null : resp.Body;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own timeout fired (not the caller's token); surface it as a transient HTTP error
            // so the poll loop's retry/fallback handling treats it like any other fetch failure.
            throw new HttpRequestException($"Netgear CM raw fetch of {pathAndQuery} timed out after {TimeoutSeconds}s");
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException)
        {
            throw new HttpRequestException($"Netgear CM raw fetch of {pathAndQuery} failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Open a TCP connection and perform one HTTP/1.0 GET, returning the full raw response bytes
    /// (headers and body) read until the server closes the connection. No Accept-Encoding is sent,
    /// so the body comes back uncompressed and plain.
    /// </summary>
    private static async Task<byte[]> RawHttpGetAsync(
        string host, int port, string pathAndQuery, string? authHeader, string? cookie,
        CancellationToken cancellationToken)
    {
        var request = new StringBuilder();
        request.Append("GET ").Append(pathAndQuery).Append(" HTTP/1.0\r\n");
        request.Append("Host: ").Append(host).Append("\r\n");
        if (authHeader != null)
            request.Append("Authorization: ").Append(authHeader).Append("\r\n");
        if (cookie != null)
            request.Append("Cookie: ").Append(cookie).Append("\r\n");
        request.Append("Accept: */*\r\n");
        request.Append("Connection: close\r\n");
        request.Append("\r\n");

        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(host, port, cancellationToken);
        await using var stream = tcp.GetStream();

        var reqBytes = Encoding.ASCII.GetBytes(request.ToString());
        await stream.WriteAsync(reqBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxRawResponseBytes)
                break;
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// A status code, leniently parsed headers, and decoded body extracted from a raw HTTP
    /// response by <see cref="ParseRawHttpResponse"/>.
    /// </summary>
    internal sealed record RawHttpResponse(int StatusCode, IReadOnlyDictionary<string, string> Headers, string Body);

    /// <summary>
    /// Parse a raw HTTP response leniently, tolerating the malformations seen from NET-DK modems:
    /// header lines whose leading bytes were dropped (a corrupted but colon-bearing name is kept
    /// as-is; a line that lost its colon entirely is skipped rather than aborting the whole
    /// response), and bare-LF line endings. The status code is taken from the first 3-digit token
    /// on the status line, so a clipped "HTTP/1.1" prefix still yields the code. The body is decoded
    /// as UTF-8; when a valid Content-Length survives it bounds the body, otherwise everything after
    /// the header separator is returned (the modem closes the connection to signal the end).
    /// </summary>
    internal static RawHttpResponse ParseRawHttpResponse(byte[] data, int length)
    {
        length = Math.Min(length, data.Length);

        var (headerEnd, bodyStart) = FindHeaderBodySplit(data, length);
        var headerText = Encoding.Latin1.GetString(data, 0, headerEnd);
        var lines = headerText.Split('\n');

        int status = 0;
        if (lines.Length > 0)
        {
            foreach (var token in lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length == 3 && int.TryParse(token, out var s) && s is >= 100 and < 600)
                {
                    status = s;
                    break;
                }
            }
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0)
                continue;
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue; // dropped-colon corruption - skip rather than reject the whole response
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Length == 0)
                continue;
            headers[name] = headers.TryGetValue(name, out var existing) ? existing + ", " + value : value;
        }

        int bodyLength = Math.Max(0, length - bodyStart);
        if (headers.TryGetValue("Content-Length", out var clRaw)
            && int.TryParse(clRaw.Trim(), out var contentLength)
            && contentLength >= 0 && contentLength <= bodyLength)
        {
            bodyLength = contentLength;
        }

        var body = Encoding.UTF8.GetString(data, bodyStart, bodyLength);
        return new RawHttpResponse(status, headers, body);
    }

    /// <summary>
    /// Locate the header/body boundary, preferring the RFC CRLFCRLF separator and falling back to
    /// the bare-LFLF some embedded servers emit. Returns the end of the header block and the start
    /// of the body; when no separator is found the whole buffer is treated as headers.
    /// </summary>
    private static (int HeaderEnd, int BodyStart) FindHeaderBodySplit(byte[] data, int length)
    {
        for (int i = 0; i + 3 < length; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                return (i, i + 4);
        }
        for (int i = 0; i + 1 < length; i++)
        {
            if (data[i] == '\n' && data[i + 1] == '\n')
                return (i, i + 2);
        }
        return (length, length);
    }

    internal static CableModemStats ParseDocsisStatus(string html, CmPollContext context)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var stats = new CableModemStats
        {
            Timestamp = DateTime.UtcNow,
            DeviceHost = context.Host,
            DeviceName = context.Name,
            DeviceModel = "Netgear",
        };

        // Parse downstream table using header-based column mapping
        var dsTable = doc.DocumentNode.SelectSingleNode("//table[@id='dsTable']");
        if (dsTable != null)
        {
            var rows = dsTable.SelectNodes(".//tr");
            if (rows is { Count: >= 2 })
            {
                var headerCells = rows[0].SelectNodes(".//td | .//th");
                var headers = headerCells?.Select(c => NormalizeHeader(c.InnerText)).ToList() ?? new();

                for (int i = 1; i < rows.Count; i++)
                {
                    var cells = rows[i].SelectNodes(".//td");
                    if (cells == null || cells.Count != headers.Count) continue;

                    var channel = new DsChannel();
                    for (int j = 0; j < headers.Count; j++)
                    {
                        var val = cells[j].InnerText.Trim();
                        switch (headers[j])
                        {
                            case "channel": channel.ChannelId = ParseInt(val); break;
                            case "channelid": channel.ChannelId = ParseInt(val); break;
                            case "lockstatus": channel.LockStatus = val; break;
                            case "modulation": channel.Modulation = val; break;
                            case "frequency": channel.Frequency = ParseFrequency(val); break;
                            case "power": channel.Power = ParseDouble(val); break;
                            case "snr": case "snr/mer": channel.Snr = ParseDouble(val); break;
                            case "correctables": case "corrected": channel.Correctables = ParseLong(val); break;
                            case "uncorrectables": channel.Uncorrectables = ParseLong(val); break;
                        }
                    }
                    stats.DownstreamChannels.Add(channel);
                }
            }
        }

        // Parse upstream table using header-based column mapping
        var usTable = doc.DocumentNode.SelectSingleNode("//table[@id='usTable']");
        if (usTable != null)
        {
            var rows = usTable.SelectNodes(".//tr");
            if (rows is { Count: >= 2 })
            {
                var headerCells = rows[0].SelectNodes(".//td | .//th");
                var headers = headerCells?.Select(c => NormalizeHeader(c.InnerText)).ToList() ?? new();

                for (int i = 1; i < rows.Count; i++)
                {
                    var cells = rows[i].SelectNodes(".//td");
                    if (cells == null || cells.Count != headers.Count) continue;

                    var channel = new UsChannel();
                    for (int j = 0; j < headers.Count; j++)
                    {
                        var val = cells[j].InnerText.Trim();
                        switch (headers[j])
                        {
                            case "channel": channel.ChannelId = ParseInt(val); break;
                            case "channelid": channel.ChannelId = ParseInt(val); break;
                            case "lockstatus": channel.LockStatus = val; break;
                            case "uschanneltype": case "channeltype": channel.ChannelType = val; break;
                            case "frequency": channel.Frequency = ParseFrequency(val); break;
                            case "power": channel.Power = ParseDouble(val); break;
                            case "symbolrate": channel.SymbolRate = ParseSymbolRate(val); break;
                        }
                    }
                    stats.UpstreamChannels.Add(channel);
                }
            }
        }

        // Models that build the tables client-side (e.g. CM700 on DocsisStatus.htm) leave the
        // server-rendered tables empty, so fall back to the JavaScript tagValueList data.
        ParseTagValueTables(html, stats);

        return stats;
    }

    /// <summary>
    /// Parse channel data from the JavaScript tagValueList assignments used by models that
    /// render the DOCSIS tables client-side (e.g. CM700). Only runs for tables the
    /// HtmlAgilityPack pass left empty, so it never overrides server-rendered data.
    /// </summary>
    private static void ParseTagValueTables(string html, CableModemStats stats)
    {
        if (stats.DownstreamChannels.Count == 0)
        {
            var match = DsTagValueListRx.Match(html);
            if (match.Success)
            {
                // Per channel: Channel, Lock Status, Modulation, Channel ID, Frequency, Power,
                // SNR, and (on firmware that reports them) Correctables, UnCorrectables.
                foreach (var f in ChunkTagValues(match.Groups[1].Value))
                {
                    stats.DownstreamChannels.Add(new DsChannel
                    {
                        LockStatus = f[1],
                        Modulation = f[2],
                        ChannelId = ParseInt(f[3]),
                        Frequency = ParseFrequency(f[4]),
                        Power = ParseDouble(f[5]),
                        Snr = ParseDouble(f[6]),
                        Correctables = f.Count > 7 ? ParseLong(f[7]) : 0,
                        Uncorrectables = f.Count > 8 ? ParseLong(f[8]) : 0,
                    });
                }
            }
        }

        if (stats.UpstreamChannels.Count == 0)
        {
            var match = UsTagValueListRx.Match(html);
            if (match.Success)
            {
                // Per channel: Channel, Lock Status, US Channel Type, Channel ID, Symbol Rate,
                // Frequency, Power.
                foreach (var f in ChunkTagValues(match.Groups[1].Value))
                {
                    stats.UpstreamChannels.Add(new UsChannel
                    {
                        LockStatus = f[1],
                        ChannelType = f[2],
                        ChannelId = ParseInt(f[3]),
                        SymbolRate = ParseSymbolRate(f[4]),
                        Frequency = ParseFrequency(f[5]),
                        Power = ParseDouble(f[6]),
                    });
                }
            }
        }

        // The CM2050V's .htm page carries two further tables - OFDM downstream and OFDMA upstream -
        // in their own Init functions. These are separate channels, so they're always appended
        // (a modem with these has its SC-QAM/ATDMA channels populated above already).
        var dsOfdm = DsOfdmTagValueListRx.Match(html);
        if (dsOfdm.Success)
        {
            // Per channel: Channel, Lock Status, Profile IDs, Channel ID, Frequency, Power, SNR/MER,
            // Active Subcarrier Range, Unerrored, Correctable, Uncorrectable codewords. The OFDM
            // codeword counts run to the billions and would swamp the SC-QAM correctable/
            // uncorrectable totals, so they are intentionally left out of the aggregates.
            foreach (var f in ChunkTagValues(dsOfdm.Groups[1].Value, minPerChannel: 7))
            {
                stats.DownstreamChannels.Add(new DsChannel
                {
                    LockStatus = f[1],
                    Modulation = "OFDM",
                    ChannelId = ParseInt(f[3]),
                    Frequency = ParseFrequency(f[4]),
                    Power = ParseDouble(f[5]),
                    Snr = ParseDouble(f[6]),
                });
            }
        }

        var usOfdma = UsOfdmaTagValueListRx.Match(html);
        if (usOfdma.Success)
        {
            // Per channel: Channel, Lock Status, Profile IDs, Channel ID, Frequency, Power. OFDMA
            // has no fixed symbol rate.
            foreach (var f in ChunkTagValues(usOfdma.Groups[1].Value, minPerChannel: 6))
            {
                stats.UpstreamChannels.Add(new UsChannel
                {
                    LockStatus = f[1],
                    ChannelType = "OFDMA",
                    ChannelId = ParseInt(f[3]),
                    Frequency = ParseFrequency(f[4]),
                    Power = ParseDouble(f[5]),
                });
            }
        }
    }

    /// <summary>
    /// Split a Netgear tagValueList - a leading channel count followed by pipe-delimited
    /// per-channel fields - into one field list per channel. A trailing pipe leaves an empty
    /// token that is ignored. Returns nothing if the count is missing/invalid or the fields
    /// don't divide into per-channel groups of at least <paramref name="minPerChannel"/> columns
    /// (7 for the SC-QAM/ATDMA tables, fewer for the OFDMA upstream table).
    /// </summary>
    private static IEnumerable<List<string>> ChunkTagValues(string tagValueList, int minPerChannel = 7)
    {
        var tokens = tagValueList.Split('|');
        if (tokens.Length < 2 || !int.TryParse(tokens[0].Trim(), out var count) || count <= 0)
            yield break;

        var fields = tokens.Skip(1).ToList();
        while (fields.Count > 0 && string.IsNullOrEmpty(fields[^1]))
            fields.RemoveAt(fields.Count - 1);

        var perChannel = fields.Count / count;
        if (perChannel < minPerChannel)
            yield break;

        for (int c = 0; c < count; c++)
        {
            var start = c * perChannel;
            if (start + perChannel > fields.Count)
                yield break;
            // Trim each field to mirror the server-rendered path (InnerText.Trim()). Lock
            // status is matched exactly, so stray whitespace would otherwise zero out every
            // locked count and null the power/SNR averages.
            yield return fields.GetRange(start, perChannel).Select(f => f.Trim()).ToList();
        }
    }

    /// <summary>
    /// Normalize header text for matching: lowercase, strip whitespace, slashes, and non-alpha chars.
    /// E.g. "Lock Status" → "lockstatus", "SNR/MER" → "snr/mer", "Channel ID" → "channelid"
    /// </summary>
    private static string NormalizeHeader(string text)
    {
        return text.Trim().Replace(" ", "").Replace("\n", "").Replace("\r", "").ToLowerInvariant();
    }

    /// <summary>
    /// Parse integer from text, stripping any non-numeric content.
    /// </summary>
    private static int ParseInt(string text)
    {
        var cleaned = StripUnits(text);
        return int.TryParse(cleaned, out var val) ? val : 0;
    }

    /// <summary>
    /// Parse long from text, stripping any non-numeric content.
    /// </summary>
    private static long ParseLong(string text)
    {
        var cleaned = StripUnits(text);
        return long.TryParse(cleaned, out var val) ? val : 0;
    }

    /// <summary>
    /// Parse double from text, stripping units like dBmV, dB.
    /// </summary>
    private static double? ParseDouble(string text)
    {
        var cleaned = StripUnits(text);
        return double.TryParse(cleaned, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : null;
    }

    /// <summary>
    /// Parse frequency value, stripping " Hz" suffix and returning as long (Hz).
    /// </summary>
    private static long ParseFrequency(string text)
    {
        var cleaned = StripUnits(text);
        return long.TryParse(cleaned, out var val) ? val : 0;
    }

    /// <summary>
    /// Parse symbol rate, stripping " Ksym/sec" suffix.
    /// </summary>
    private static long ParseSymbolRate(string text)
    {
        var cleaned = StripUnits(text);
        return long.TryParse(cleaned, out var val) ? val : 0;
    }

    /// <summary>
    /// Remove common unit suffixes from cable modem values.
    /// Handles: dBmV, dB, Hz, Ksym/sec, and whitespace.
    /// </summary>
    private static string StripUnits(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var cleaned = text.Trim();

        // Remove common unit suffixes (order matters - longer first)
        string[] units = { "Ksym/sec", "dBmV", "dB", "Hz", "Msym/sec" };
        foreach (var unit in units)
        {
            var idx = cleaned.IndexOf(unit, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                cleaned = cleaned[..idx];
                break;
            }
        }

        return cleaned.Trim();
    }
}
