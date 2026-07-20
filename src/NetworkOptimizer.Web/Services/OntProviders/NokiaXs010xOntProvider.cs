using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;

namespace NetworkOptimizer.Web.Services.OntProviders;

/// <summary>
/// ONT provider for the Nokia (Alcatel-Lucent, vendor ID "ALCL") XS-010X-Q XGS-PON
/// ONT. The GponForm web API is shared across Nokia's box ONTs, so the reported model
/// and XGS-PON type are hardcoded to the XS-010X-Q (the device reports neither its own
/// model nor a line rate); a GPON sibling like the G-010G-Q would need those adjusted.
/// The device serves a plain-HTTP nginx
/// UI whose only data-bearing page is moreinfo.html, backed by a small JSON API:
///
///   1. POST /GponForm/Login_GetConfig (token=token) -> {"nonce":..,"saltval":..}
///   2. cmt = sha256(username + saltval + password), lowercase hex
///   3. POST /GponForm/LoginForm (cmt=..&nonce=..) -> {"login_result":..,"cookieid":..}
///   4. POST /GponForm/getUpdateinfo (token=token) with Cookie: sessionid=&lt;cookieid&gt;
///      -> {"CurrentPonPw","VendorID","VersionID","SerialNum","Mac","ActiveSwVer",
///          "StandbySwVer","RxOptPwr"}
///
/// getUpdateinfo authorization differs by firmware, so two flows are tried and the winner is
/// cached per config (see <see cref="AuthFlow"/>). <see cref="AuthFlow.Direct"/> calls
/// getUpdateinfo with just the session cookie straight after login - the minimal request the one
/// firmware confirmed working (@Liosnel's) accepts, kept untouched. <see cref="AuthFlow.PageWalk"/>
/// handles a T-Fiber/Metronet CLEI variant that forces a PON-password page after login and gates
/// getUpdateinfo behind walking through it: GET /ponpasswd.html, POST /GponForm/ponpasswd_GetConfig,
/// GET /moreinfo.html, each carrying the session cookie and the browser's Referer/Origin/
/// X-Requested-With headers, then getUpdateinfo with those headers too.
///
/// The device exposes no TX power, temperature, or explicit link state; the receive
/// optical power (RxOptPwr, dBm) is the one health metric it reports. Login credentials
/// default to admin/1234 on these units but are user-configurable.
/// </summary>
public sealed class NokiaXs010xOntProvider : IOntProvider
{
    public string ProviderKey => "nokia-xs010x-q";
    public string DisplayName => "Nokia XS-010X-Q (HTTP)";

    private const int TimeoutSeconds = 15;
    private const string LoginConfigPath = "/GponForm/Login_GetConfig";
    private const string LoginPath = "/GponForm/LoginForm";
    private const string UpdateInfoPath = "/GponForm/getUpdateinfo";
    private const string PonPasswdConfigPath = "/GponForm/ponpasswd_GetConfig";
    private const string FormContentType = "application/x-www-form-urlencoded";
    private const string LoginPagePath = "/login.html";
    private const string PonPasswdPagePath = "/ponpasswd.html";
    private const string MoreInfoPagePath = "/moreinfo.html";
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36";

    /// <summary>
    /// How a given firmware authorizes the getUpdateinfo call. Determined on first contact and
    /// cached per config in <see cref="_flowCache"/>; re-detected after a process restart.
    /// </summary>
    private enum AuthFlow
    {
        /// <summary>getUpdateinfo with just the cookie, straight after login (@Liosnel's firmware).</summary>
        Direct,

        /// <summary>Walk the forced PON-password pages before getUpdateinfo (T-Fiber/Metronet CLEI variant).</summary>
        PageWalk,
    }

    private readonly ILogger<NokiaXs010xOntProvider> _logger;

    /// <summary>
    /// Remembers the getUpdateinfo <see cref="AuthFlow"/> that last succeeded per
    /// OntConfiguration.Id so later polls go straight to it instead of re-probing the wrong flow
    /// (and, for the page walk, its extra requests) every interval. Re-detected after a restart.
    /// </summary>
    private readonly ConcurrentDictionary<int, AuthFlow> _flowCache = new();

    public NokiaXs010xOntProvider(ILogger<NokiaXs010xOntProvider> logger)
    {
        _logger = logger;
    }

    public async Task<OntStats?> PollAsync(OntPollContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
        {
            _logger.LogWarning("Nokia XS-010X-Q ONT poll requested but Host is empty (config {Id})", context.Id);
            return null;
        }

        try
        {
            using var client = CreateClient();
            var baseUrl = BuildBaseUrl(context);
            var stats = await FetchStatsAsync(client, baseUrl, context, cancellationToken);

            if (stats is null)
            {
                _logger.LogWarning("Nokia XS-010X-Q ONT {Name}: login failed", context.Name);
                return null;
            }

            _logger.LogDebug(
                "Nokia XS-010X-Q ONT {Name} polled: Rx={Rx} dBm, SN={Sn}, Link={Link}",
                context.Name, stats.RxPowerDbm?.ToString("F1") ?? "-",
                stats.VendorSn ?? "-", stats.LinkState ?? "-");

            return stats;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error polling Nokia XS-010X-Q ONT {Name} at {Host}",
                context.Name, context.ConfiguredHost ?? context.Host);
            return null;
        }
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(
        OntPollContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
            return (false, "Host is empty");

        try
        {
            using var client = CreateClient();
            var baseUrl = BuildBaseUrl(context);
            var stats = await FetchStatsAsync(client, baseUrl, context, cancellationToken);

            if (stats is null)
                return (false, "Login failed - check username/password (default is admin/1234)");

            if (stats.RxPowerDbm is null)
                return (false, "Logged in but response did not contain the expected RxOptPwr field");

            return (true, $"Connected (HTTP) - Nokia XS-010X-Q, RX: {stats.RxPowerDbm.Value:F1} dBm");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Logs in once, then reads getUpdateinfo trying the resolved <see cref="AuthFlow"/> order,
    /// returning the first result that carries an RX reading and caching the flow that produced
    /// it. Returns the last built stats (which may lack RX) if a flow logged in but returned no
    /// data, or null if login itself failed.
    /// </summary>
    private async Task<OntStats?> FetchStatsAsync(
        HttpClient client, string baseUrl, OntPollContext context, CancellationToken ct)
    {
        var cookieId = await LoginAsync(client, baseUrl, context, ct);
        if (cookieId is null)
            return null;

        OntStats? last = null;
        foreach (var flow in ResolveFlows(context))
        {
            var infoJson = flow == AuthFlow.PageWalk
                ? await GetUpdateInfoViaWalkAsync(client, baseUrl, cookieId, context.Name, ct)
                : await GetUpdateInfoAsync(client, baseUrl, cookieId, context.Name, MoreInfoPagePath, browserHeaders: false, ct);

            var stats = new OntStats
            {
                Timestamp = DateTime.UtcNow,
                DeviceHost = context.ConfiguredHost ?? context.Host,
                DeviceName = context.Name,
                DeviceModel = "Nokia XS-010X-Q",
            };
            ApplyUpdateInfo(infoJson, stats);
            if (stats.RxPowerDbm is not null)
            {
                if (context.Id > 0)
                    _flowCache[context.Id] = flow;
                return stats;
            }

            last = stats;
        }

        return last;
    }

    /// <summary>
    /// The getUpdateinfo flows to try, in order: the one cached for this config first (so a
    /// known-good firmware never re-probes), then the other as a fallback in case the cache is
    /// empty or the firmware behavior changed. Uncached configs try Direct first - it's one
    /// request and the only flow confirmed working on real hardware (@Liosnel's), so only a
    /// variant that needs the walk pays for the extra probe, once, until it's cached.
    /// </summary>
    private IReadOnlyList<AuthFlow> ResolveFlows(OntPollContext context)
    {
        if (context.Id > 0 && _flowCache.TryGetValue(context.Id, out var cached))
            return cached == AuthFlow.PageWalk
                ? new[] { AuthFlow.PageWalk, AuthFlow.Direct }
                : new[] { AuthFlow.Direct, AuthFlow.PageWalk };

        return new[] { AuthFlow.Direct, AuthFlow.PageWalk };
    }

    /// <summary>
    /// Runs the three-step GponForm login and returns the session cookie id, or null if
    /// authentication fails. The cookie is delivered inside the LoginForm JSON body (the
    /// page's script clears the Set-Cookie header), so it is threaded back manually as a
    /// Cookie header on later requests rather than via a CookieContainer.
    /// </summary>
    private async Task<string?> LoginAsync(
        HttpClient client, string baseUrl, OntPollContext context, CancellationToken ct)
    {
        var username = string.IsNullOrWhiteSpace(context.Username) ? "admin" : context.Username;
        var password = context.Password ?? "";

        string configJson;
        int configStatus;
        using (var request = BuildRequest(HttpMethod.Post, $"{baseUrl}{LoginConfigPath}", "token=token", baseUrl, LoginPagePath, cookieId: null, browserHeaders: true))
        using (var response = await client.SendAsync(request, ct))
        {
            configStatus = (int)response.StatusCode;
            configJson = await response.Content.ReadAsStringAsync(ct);
        }

        var (nonce, saltval) = ParseLoginConfig(configJson);
        _logger.LogDebug("Nokia XS-010X-Q ONT {Name}: Login_GetConfig HTTP {Status}, nonce={HasNonce}, saltval='{Salt}', body={Body}",
            context.Name, configStatus, !string.IsNullOrEmpty(nonce), saltval ?? "", Preview(configJson));
        if (string.IsNullOrEmpty(nonce))
            return null;

        var cmt = ComputeCmt(username, saltval ?? "", password);
        var body = $"cmt={cmt}&nonce={Uri.EscapeDataString(nonce)}";

        string loginJson;
        int loginStatus;
        string? setCookie;
        using (var request = BuildRequest(HttpMethod.Post, $"{baseUrl}{LoginPath}", body, baseUrl, LoginPagePath, cookieId: null, browserHeaders: true))
        using (var response = await client.SendAsync(request, ct))
        {
            loginStatus = (int)response.StatusCode;
            loginJson = await response.Content.ReadAsStringAsync(ct);
            // Diagnostic: the reference flow delivers the cookie in the JSON body and clears the
            // Set-Cookie header, but some firmware may set a real (differently named) cookie via
            // header. Log it so a non-working unit's logs reveal which path it uses.
            setCookie = response.Headers.TryGetValues("Set-Cookie", out var cookieValues)
                ? string.Join(" | ", cookieValues)
                : null;
        }

        var cookieId = ParseCookieId(loginJson);
        _logger.LogDebug("Nokia XS-010X-Q ONT {Name}: LoginForm HTTP {Status}, gotCookie={HasCookie}, setCookie={SetCookie}, body={Body}",
            context.Name, loginStatus, cookieId != null, setCookie ?? "(none)", Preview(loginJson));
        return cookieId;
    }

    /// <summary>
    /// Walks the forced PON-password pages (GET /ponpasswd.html, POST ponpasswd_GetConfig,
    /// GET /moreinfo.html) so firmware that gates the data call behind them advances the session,
    /// then reads getUpdateinfo. The session cookie and browser headers ride every step. Walk
    /// responses are discarded; a step that 404s on firmware without the page is harmless (that
    /// firmware just uses the Direct flow instead).
    /// </summary>
    private async Task<string> GetUpdateInfoViaWalkAsync(
        HttpClient client, string baseUrl, string cookieId, string deviceName, CancellationToken ct)
    {
        await WalkStepAsync(client, BuildRequest(HttpMethod.Get, $"{baseUrl}{PonPasswdPagePath}", null, baseUrl, LoginPagePath, cookieId, browserHeaders: true), ct);
        await WalkStepAsync(client, BuildRequest(HttpMethod.Post, $"{baseUrl}{PonPasswdConfigPath}", "token=token", baseUrl, PonPasswdPagePath, cookieId, browserHeaders: true), ct);
        await WalkStepAsync(client, BuildRequest(HttpMethod.Get, $"{baseUrl}{MoreInfoPagePath}", null, baseUrl, PonPasswdPagePath, cookieId, browserHeaders: true), ct);
        return await GetUpdateInfoAsync(client, baseUrl, cookieId, deviceName, PonPasswdPagePath, browserHeaders: true, ct);
    }

    private static async Task WalkStepAsync(HttpClient client, HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        using (await client.SendAsync(request, ct)) { }
    }

    private async Task<string> GetUpdateInfoAsync(
        HttpClient client, string baseUrl, string cookieId, string deviceName, string refererPath, bool browserHeaders, CancellationToken ct)
    {
        using var request = BuildRequest(HttpMethod.Post, $"{baseUrl}{UpdateInfoPath}", "token=token", baseUrl, refererPath, cookieId, browserHeaders);

        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        _logger.LogDebug("Nokia XS-010X-Q ONT {Name}: getUpdateinfo HTTP {Status}, body={Body}",
            deviceName, (int)response.StatusCode, Preview(body));
        return body;
    }

    /// <summary>
    /// Builds a GponForm request. The session cookie is attached whenever one is supplied. When
    /// <paramref name="browserHeaders"/> is set the request also mirrors the device page's XHR
    /// traffic - a Referer of the page the call is made from and, for the POSTs, Origin and
    /// X-Requested-With: XMLHttpRequest - which the T-Fiber/Metronet CLEI variant needs. The
    /// Direct getUpdateinfo passes false to stay the bare cookie-only request the one confirmed
    /// firmware accepts. Pass <paramref name="formBody"/> null for a GET (a page navigation).
    /// </summary>
    private static HttpRequestMessage BuildRequest(
        HttpMethod method, string url, string? formBody, string baseUrl, string refererPath, string? cookieId, bool browserHeaders)
    {
        var request = new HttpRequestMessage(method, url);

        if (formBody is not null)
            request.Content = new StringContent(formBody, Encoding.UTF8, FormContentType);

        if (browserHeaders)
        {
            request.Headers.TryAddWithoutValidation("Referer", $"{baseUrl}{refererPath}");
            if (formBody is not null)
            {
                request.Headers.TryAddWithoutValidation("Origin", baseUrl);
                request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            }
        }

        if (!string.IsNullOrEmpty(cookieId))
            request.Headers.TryAddWithoutValidation("Cookie", $"sessionid={cookieId}");

        return request;
    }

    /// <summary>Trims a raw device response for diagnostic logging.</summary>
    private static string Preview(string s) =>
        string.IsNullOrEmpty(s) ? "(empty)" : (s.Length > 800 ? s[..800] + "...(truncated)" : s);

    /// <summary>
    /// cmt = sha256(username + saltval + password) as lowercase hex. Verified against a live
    /// unit: sha256("admin" + "ea" + "1234") == b7290cb3...f22fc7.
    /// </summary>
    internal static string ComputeCmt(string username, string saltval, string password)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(username + saltval + password));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>Extracts nonce and saltval from the Login_GetConfig JSON response.</summary>
    internal static (string? Nonce, string? Salt) ParseLoginConfig(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (null, null);

            return (GetStringProp(doc.RootElement, "nonce"), GetStringProp(doc.RootElement, "saltval"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Reads the session cookie id from the LoginForm JSON response. Returns null when the
    /// device reports a login error ({"login_result":"error"}) or omits the cookie.
    /// </summary>
    internal static string? ParseCookieId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var result = GetStringProp(doc.RootElement, "login_result");
            if (string.Equals(result, "error", StringComparison.OrdinalIgnoreCase))
                return null;

            var cookieId = GetStringProp(doc.RootElement, "cookieid");
            return string.IsNullOrWhiteSpace(cookieId) ? null : cookieId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Maps the getUpdateinfo JSON onto OntStats. Only RxOptPwr, VendorID, VersionID and
    /// SerialNum carry monitoring value; the device reports no TX power, temperature, or
    /// explicit link state, so operational status is inferred from a present RX reading.
    /// </summary>
    internal static void ApplyUpdateInfo(string json, OntStats stats)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return;

            var root = doc.RootElement;

            stats.RxPowerDbm = ParseDouble(GetStringProp(root, "RxOptPwr")) ?? stats.RxPowerDbm;

            if (GetStringProp(root, "VendorID") is { Length: > 0 } vendor)
                stats.VendorName = vendor;

            if (GetStringProp(root, "VersionID") is { Length: > 0 } version)
                stats.VendorPn = version;

            if (GetStringProp(root, "SerialNum") is { Length: > 0 } serial)
                stats.VendorSn = serial;

            // XS-010X-Q is a 10G-symmetric XGS-PON ONT; the device exposes no line rate,
            // so the PON type is taken from the model rather than derived from a rate field.
            stats.PonType = "XGS-PON";

            // No link-state field is exposed. A successful authenticated read that returns a
            // real optical-power value means the ONU is powered and seeing downstream light,
            // which we surface as Up; without an RxOptPwr reading we leave status unknown.
            if (stats.RxPowerDbm is not null)
            {
                stats.OperationalStatus = "Up";
                stats.LinkState = "Up";
            }
        }
        catch (JsonException) { }
    }

    private static string? GetStringProp(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static double? ParseDouble(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var val) ? val : null;

    internal static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
        // Mirror the working curl flow: a fresh TCP connection per request. These GponForm
        // boxes can tie the login session to the connection, so keep-alive reuse across the
        // login -> getUpdateinfo steps can return an empty/unauthenticated response.
        client.DefaultRequestHeaders.ConnectionClose = true;
        // A browser User-Agent - the picky CLEI variant appears to sniff it; harmless elsewhere.
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        return client;
    }

    private static string BuildBaseUrl(OntPollContext context)
    {
        var port = context.Port > 0 ? context.Port : 80;
        var portSuffix = port == 80 ? "" : $":{port}";
        return $"http://{context.Host}{portSuffix}";
    }
}
