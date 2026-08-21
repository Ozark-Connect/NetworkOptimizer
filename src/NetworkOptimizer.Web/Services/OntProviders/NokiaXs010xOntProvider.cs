using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.Monitoring;

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
/// Some firmware accepts this sequence from curl and from a browser but answered 401 ->
/// /login.html when HttpClient sent the logically identical requests. Root cause (found by
/// @jakerobb on #929): those units answer the login calls with a malformed
/// "Set-Cookie: Path=/; HttpOnly" header carrying no name=value pair. A cookie-enabled handler
/// parses "Path=/" as a literal cookie, and once the CookieContainer holds anything for the
/// host it silently replaces the hand-set "Cookie: sessionid=..." header - so the device never
/// saw the session id. curl and raw sockets have no cookie engine and browsers discard the
/// malformed header, which is why every other client worked. <see cref="CreateClient"/>
/// therefore disables handler cookies so the hand-set header goes out untouched.
///
/// Firmware behavior demonstrably varies across units, so two flows are kept and the winner is
/// cached per config (see <see cref="AuthFlow"/>). <see cref="AuthFlow.Direct"/> is the plain
/// HttpClient sequence as shipped in v2.1.1/v2.2.0 (confirmed working on @Liosnel's unit) plus
/// the cookie fix, which only changes the wire when a device emits Set-Cookie on login.
/// <see cref="AuthFlow.CurlReplay"/> is defense in depth: it writes the raw HTTP requests of
/// the tester-proven curl script over a plain TCP socket, byte-for-byte (same headers, order
/// and casing; fresh connection per request like separate curl invocations), including the
/// forced PON-password page walk some firmware presents: GET /login.html, login,
/// GET /ponpasswd.html, POST /GponForm/ponpasswd_GetConfig, GET /moreinfo.html, then
/// getUpdateinfo. It is immune to any handler-level rewriting by construction.
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
    /// How a given firmware's getUpdateinfo is reached. Determined on first contact and
    /// cached per config in <see cref="_flowCache"/>; re-detected after a process restart.
    /// </summary>
    private enum AuthFlow
    {
        /// <summary>Plain HttpClient login + cookie-only getUpdateinfo (handler cookies off so
        /// the hand-set session header survives malformed device Set-Cookie headers).</summary>
        Direct,

        /// <summary>Raw-socket byte-for-byte replay of the tester-proven curl script, immune to
        /// handler-level rewriting by construction.</summary>
        CurlReplay,
    }

    private readonly ILogger<NokiaXs010xOntProvider> _logger;

    /// <summary>
    /// Remembers the <see cref="AuthFlow"/> that last succeeded per OntConfiguration.Id so
    /// later polls go straight to it instead of re-probing the wrong flow (and, for the curl
    /// replay, its extra requests) every interval. Re-detected after a restart.
    /// </summary>
    private readonly ConcurrentDictionary<string, AuthFlow> _flowCache = new();

    public NokiaXs010xOntProvider(ILogger<NokiaXs010xOntProvider> logger)
    {
        _logger = logger;
    }

    public async Task<PollResult<OntStats>> PollAsync(OntPollContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
        {
            _logger.LogWarning("Nokia XS-010X-Q ONT poll requested but Host is empty (config {Id})", context.CacheKey);
            return PollResult<OntStats>.Failed("No address is configured for this device.");
        }

        try
        {
            using var client = CreateClient();
            var baseUrl = BuildBaseUrl(context);
            var stats = await FetchStatsAsync(client, baseUrl, context, cancellationToken);

            if (stats is null)
            {
                _logger.LogWarning("Nokia XS-010X-Q ONT {Name}: login failed", context.Name);
                return PollResult<OntStats>.Failed($"No stats could be read from {(context.ConfiguredHost ?? context.Host)}.");
            }

            _logger.LogDebug(
                "Nokia XS-010X-Q ONT {Name} polled: Rx={Rx} dBm, SN={Sn}, Link={Link}",
                context.Name, stats.RxPowerDbm?.ToString("F1") ?? "-",
                stats.VendorSn ?? "-", stats.LinkState ?? "-");

            return PollResult<OntStats>.Ok(stats);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error polling Nokia XS-010X-Q ONT {Name} at {Host}",
                context.Name, context.ConfiguredHost ?? context.Host);
            return PollResult<OntStats>.Failed(HttpFailureSummary.Describe(ex, (context.ConfiguredHost ?? context.Host)));
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
            return (false, HttpFailureSummary.Describe(ex, context.ConfiguredHost ?? context.Host));
        }
    }

    /// <summary>
    /// Reads getUpdateinfo trying the resolved <see cref="AuthFlow"/> order, returning the first
    /// result that carries an RX reading and caching the flow that produced it. Each flow does its
    /// own login so a probe of the wrong flow (e.g. a Direct getUpdateinfo that 401s on the CLEI
    /// variant, which forces a PON-password session state) can't poison the next flow's session.
    /// Returns the last built stats (which may lack RX) if a flow logged in but returned no data,
    /// or null if every login failed.
    /// </summary>
    private async Task<OntStats?> FetchStatsAsync(
        HttpClient client, string baseUrl, OntPollContext context, CancellationToken ct)
    {
        OntStats? last = null;
        foreach (var flow in ResolveFlows(context))
        {
            string? infoJson;
            if (flow == AuthFlow.CurlReplay)
            {
                infoJson = await GetUpdateInfoViaCurlReplayAsync(baseUrl, context, ct);
            }
            else
            {
                var cookieId = await LoginAsync(client, baseUrl, context, ct);
                infoJson = cookieId is null
                    ? null
                    : await GetUpdateInfoAsync(client, baseUrl, cookieId, context.Name, ct);
            }

            if (infoJson is null)
                continue;

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
                    _flowCache[context.CacheKey] = flow;
                return stats;
            }

            last = stats;
        }

        return last;
    }

    /// <summary>
    /// The getUpdateinfo flows to try, in order: the one cached for this config first (so a
    /// known-good firmware never re-probes), then the other as a fallback in case the cache is
    /// empty or the firmware behavior changed. Uncached configs try Direct first - it's two
    /// requests and the only flow confirmed working on real hardware (@Liosnel's), so only a
    /// variant that needs the replay pays for the extra probe, once, until it's cached.
    /// </summary>
    private IReadOnlyList<AuthFlow> ResolveFlows(OntPollContext context)
    {
        if (context.Id > 0 && _flowCache.TryGetValue(context.CacheKey, out var cached))
            return cached == AuthFlow.CurlReplay
                ? new[] { AuthFlow.CurlReplay, AuthFlow.Direct }
                : new[] { AuthFlow.Direct, AuthFlow.CurlReplay };

        return new[] { AuthFlow.Direct, AuthFlow.CurlReplay };
    }

    /// <summary>
    /// Runs the three-step GponForm login and returns the session cookie id, or null if
    /// authentication fails. The cookie is delivered inside the LoginForm JSON body (the
    /// page's script clears the Set-Cookie header), so it is threaded back manually as a
    /// Cookie header on the data request rather than via a CookieContainer.
    /// </summary>
    private async Task<string?> LoginAsync(
        HttpClient client, string baseUrl, OntPollContext context, CancellationToken ct)
    {
        var username = string.IsNullOrWhiteSpace(context.Username) ? "admin" : context.Username;
        var password = context.Password ?? "";

        string configJson;
        int configStatus;
        using (var content = new StringContent("token=token", Encoding.UTF8, FormContentType))
        using (var response = await client.PostAsync($"{baseUrl}{LoginConfigPath}", content, ct))
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
        using (var content = new StringContent(body, Encoding.UTF8, FormContentType))
        using (var response = await client.PostAsync($"{baseUrl}{LoginPath}", content, ct))
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

    private async Task<string> GetUpdateInfoAsync(
        HttpClient client, string baseUrl, string cookieId, string deviceName, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}{UpdateInfoPath}")
        {
            Content = new StringContent("token=token", Encoding.UTF8, FormContentType),
        };
        request.Headers.TryAddWithoutValidation("Cookie", $"sessionid={cookieId}");

        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        _logger.LogDebug("Nokia XS-010X-Q ONT {Name}: getUpdateinfo HTTP {Status}, body={Body}",
            deviceName, (int)response.StatusCode, Preview(body));
        return body;
    }

    /// <summary>
    /// Replays the tester-proven curl sequence over raw sockets: GET /login.html, the two login
    /// POSTs, the PON-password page walk, then getUpdateinfo, every request framed byte-for-byte
    /// like curl's. Walk-page failures are ignored (firmware without the forced page just 404s
    /// them); returns the getUpdateinfo body, or null when login fails or the device is
    /// unreachable.
    /// </summary>
    private async Task<string?> GetUpdateInfoViaCurlReplayAsync(
        string baseUrl, OntPollContext context, CancellationToken ct)
    {
        var username = string.IsNullOrWhiteSpace(context.Username) ? "admin" : context.Username;
        var password = context.Password ?? "";

        await SendCurlRequestAsync(baseUrl, context, LoginPagePath, cookieId: null, refererPath: null, formBody: null, ct);

        var config = await SendCurlRequestAsync(baseUrl, context, LoginConfigPath, cookieId: null, LoginPagePath, "token=token", ct);
        if (config is null)
            return null;

        var (nonce, saltval) = ParseLoginConfig(config.Value.Body);
        if (string.IsNullOrEmpty(nonce))
        {
            _logger.LogDebug("Nokia XS-010X-Q ONT {Name}: curl-replay Login_GetConfig HTTP {Status} returned no nonce, body={Body}",
                context.Name, config.Value.Status, Preview(config.Value.Body));
            return null;
        }

        var cmt = ComputeCmt(username, saltval ?? "", password);
        var loginBody = $"cmt={cmt}&nonce={Uri.EscapeDataString(nonce)}";
        var login = await SendCurlRequestAsync(baseUrl, context, LoginPath, cookieId: null, LoginPagePath, loginBody, ct);
        if (login is null)
            return null;

        var cookieId = ParseCookieId(login.Value.Body);
        _logger.LogDebug("Nokia XS-010X-Q ONT {Name}: curl-replay LoginForm HTTP {Status}, gotCookie={HasCookie}, body={Body}",
            context.Name, login.Value.Status, cookieId != null, Preview(login.Value.Body));
        if (cookieId is null)
            return null;

        await SendCurlRequestAsync(baseUrl, context, PonPasswdPagePath, cookieId, LoginPagePath, formBody: null, ct);
        await SendCurlRequestAsync(baseUrl, context, PonPasswdConfigPath, cookieId, PonPasswdPagePath, "token=token", ct);
        await SendCurlRequestAsync(baseUrl, context, MoreInfoPagePath, cookieId, PonPasswdPagePath, formBody: null, ct);

        var info = await SendCurlRequestAsync(baseUrl, context, UpdateInfoPath, cookieId, MoreInfoPagePath, "token=token", ct);
        if (info is null)
            return null;

        _logger.LogDebug("Nokia XS-010X-Q ONT {Name}: curl-replay getUpdateinfo HTTP {Status}, body={Body}",
            context.Name, info.Value.Status, Preview(info.Value.Body));
        return info.Value.Body;
    }

    /// <summary>
    /// Sends one curl-framed request on its own TCP connection (each curl invocation in the
    /// reference script is a separate process and connection) and reads the response until the
    /// device closes the connection or the body is provably complete. Returns null on network
    /// errors or timeout so the caller can treat the step as failed without aborting the poll.
    /// </summary>
    private async Task<(int Status, string Body)?> SendCurlRequestAsync(
        string baseUrl, OntPollContext context, string path, string? cookieId, string? refererPath,
        string? formBody, CancellationToken ct)
    {
        var requestBytes = BuildCurlRequest(baseUrl, path, cookieId, refererPath, formBody);
        var port = context.Port > 0 ? context.Port : 80;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        var token = timeoutCts.Token;

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(context.Host, port, token);
            var stream = tcp.GetStream();
            await stream.WriteAsync(requestBytes, token);

            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, token);
                if (read == 0)
                    break;
                memory.Write(buffer, 0, read);
                if (IsRawResponseComplete(new ReadOnlySpan<byte>(memory.GetBuffer(), 0, (int)memory.Length)))
                    break;
            }

            return ParseRawHttpResponse(memory.ToArray());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("Nokia XS-010X-Q ONT {Name}: curl-replay request to {Path} timed out", context.Name, path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            _logger.LogDebug(ex, "Nokia XS-010X-Q ONT {Name}: curl-replay request to {Path} failed", context.Name, path);
            return null;
        }
    }

    /// <summary>
    /// Builds one request of the curl replay, byte-for-byte as curl frames it (captured against a
    /// local listener from the tester's script): request line, Host, User-Agent, Accept: */*,
    /// then Cookie, then Referer, and for POSTs Origin, X-Requested-With, Content-Length before
    /// Content-Type with no charset suffix. No Connection header (curl relies on HTTP/1.1
    /// keep-alive; the device closes the connection itself). A null <paramref name="formBody"/>
    /// makes it a GET page navigation, which carries none of the POST-only headers.
    /// </summary>
    internal static byte[] BuildCurlRequest(
        string baseUrl, string path, string? cookieId, string? refererPath, string? formBody)
    {
        var host = baseUrl["http://".Length..];
        var builder = new StringBuilder()
            .Append(formBody is null ? "GET " : "POST ").Append(path).Append(" HTTP/1.1\r\n")
            .Append("Host: ").Append(host).Append("\r\n")
            .Append("User-Agent: ").Append(BrowserUserAgent).Append("\r\n")
            .Append("Accept: */*\r\n");

        if (cookieId is not null)
            builder.Append("Cookie: sessionid=").Append(cookieId).Append("\r\n");

        if (refererPath is not null)
            builder.Append("Referer: ").Append(baseUrl).Append(refererPath).Append("\r\n");

        if (formBody is not null)
        {
            builder.Append("Origin: ").Append(baseUrl).Append("\r\n")
                .Append("X-Requested-With: XMLHttpRequest\r\n")
                .Append("Content-Length: ").Append(Encoding.ASCII.GetByteCount(formBody).ToString(CultureInfo.InvariantCulture)).Append("\r\n")
                .Append("Content-Type: ").Append(FormContentType).Append("\r\n");
        }

        builder.Append("\r\n");
        if (formBody is not null)
            builder.Append(formBody);

        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    /// <summary>
    /// Whether a raw HTTP response buffered so far is provably complete: headers received and the
    /// chunked body's terminating zero chunk seen, or the declared Content-Length satisfied. A
    /// response with neither framing can only be completed by the device closing the connection,
    /// so it reports false and the reader waits for EOF. The device answers GponForm calls with
    /// Transfer-Encoding: chunked and Connection: close, making this an early-out; the EOF path
    /// is the safety net.
    /// </summary>
    internal static bool IsRawResponseComplete(ReadOnlySpan<byte> data)
    {
        var headerEnd = data.IndexOf("\r\n\r\n"u8);
        if (headerEnd < 0)
            return false;

        var headers = Encoding.ASCII.GetString(data[..headerEnd]);
        var body = data[(headerEnd + 4)..];

        if (headers.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase))
            return TryDecodeChunked(body, out _);

        var contentLength = ParseContentLength(headers);
        return contentLength is not null && body.Length >= contentLength.Value;
    }

    /// <summary>
    /// Splits a raw HTTP/1.1 response into status code and decoded body text, de-chunking when
    /// the device uses Transfer-Encoding: chunked (it does, on every GponForm response) and
    /// otherwise honoring Content-Length or taking everything after the headers. Best-effort on
    /// truncated input: whatever body bytes arrived are returned.
    /// </summary>
    internal static (int StatusCode, string Body) ParseRawHttpResponse(byte[] raw)
    {
        var data = raw.AsSpan();
        var headerEnd = data.IndexOf("\r\n\r\n"u8);
        if (headerEnd < 0)
            return (0, "");

        var headers = Encoding.ASCII.GetString(data[..headerEnd]);
        var body = data[(headerEnd + 4)..];

        var statusCode = 0;
        var statusLine = headers.Split("\r\n", 2)[0].Split(' ');
        if (statusLine.Length >= 2)
            int.TryParse(statusLine[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out statusCode);

        if (headers.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase))
        {
            TryDecodeChunked(body, out var decoded);
            return (statusCode, Encoding.UTF8.GetString(decoded));
        }

        var contentLength = ParseContentLength(headers);
        if (contentLength is not null && body.Length > contentLength.Value)
            body = body[..contentLength.Value];

        return (statusCode, Encoding.UTF8.GetString(body));
    }

    /// <summary>
    /// Walks a chunked transfer-coded body, collecting the chunk payloads received so far into
    /// <paramref name="decoded"/>. Returns true only when the terminating zero-size chunk has
    /// arrived; on truncated or malformed input it returns false with the chunks recovered up to
    /// that point.
    /// </summary>
    private static bool TryDecodeChunked(ReadOnlySpan<byte> body, out byte[] decoded)
    {
        using var output = new MemoryStream();
        var pos = 0;
        while (true)
        {
            var lineEnd = body[pos..].IndexOf("\r\n"u8);
            if (lineEnd < 0)
                break;

            var sizeText = Encoding.ASCII.GetString(body.Slice(pos, lineEnd));
            var semicolon = sizeText.IndexOf(';');
            if (semicolon >= 0)
                sizeText = sizeText[..semicolon];

            if (!int.TryParse(sizeText.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0)
                break;

            pos += lineEnd + 2;
            if (size == 0)
            {
                decoded = output.ToArray();
                return true;
            }

            if (pos + size > body.Length)
            {
                output.Write(body[pos..]);
                break;
            }

            output.Write(body.Slice(pos, size));
            pos += size + 2;
            if (pos > body.Length)
                break;
        }

        decoded = output.ToArray();
        return false;
    }

    /// <summary>Reads the Content-Length header value out of a raw response's header block.</summary>
    private static int? ParseContentLength(string headers)
    {
        foreach (var line in headers.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line["Content-Length:".Length..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
                return length;
        }

        return null;
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
        // Handler cookies must stay off (thanks @jakerobb, #929): some firmware answers the
        // login calls with a malformed "Set-Cookie: Path=/; HttpOnly" header, which a default
        // CookieContainer parses as a literal cookie and then silently substitutes for the
        // hand-set "Cookie: sessionid=..." header, so the device never sees the session id.
        // The real session cookie arrives in the LoginForm JSON body, not in Set-Cookie, so
        // nothing legitimate is lost by disabling the cookie engine.
        var handler = new HttpClientHandler { UseCookies = false };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
        // Mirror the working curl flow: a fresh TCP connection per request. These GponForm
        // boxes can tie the login session to the connection, so keep-alive reuse across the
        // login -> getUpdateinfo steps can return an empty/unauthenticated response.
        client.DefaultRequestHeaders.ConnectionClose = true;
        return client;
    }

    private static string BuildBaseUrl(OntPollContext context)
    {
        var port = context.Port > 0 ? context.Port : 80;
        var portSuffix = port == 80 ? "" : $":{port}";
        return $"http://{context.Host}{portSuffix}";
    }
}
