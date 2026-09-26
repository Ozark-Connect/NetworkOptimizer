using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.Monitoring;

namespace NetworkOptimizer.Web.Services.CellularModemProviders;

/// <summary>
/// Cellular modem provider for Zyxel 5G/LTE CPEs on the ZCFG web API: the NR series
/// (NR7301/NR7302/NR7303, NR7101/NR7102, NR5103), the FWA series (FWA505/510/710), and the LTE
/// series (LTE3202, LTE5398, LTE7490). Zyxel's DSL and fiber routers share the API but have no
/// cellular interface, so they have no cellwan_status to read.
///
/// Signs in the way the web UI does (<c>/getRSAPublickKey</c>, then <c>/UserLogin</c>) and reads
/// <c>/cgi-bin/DAL?oid=cellwan_status</c>. Newer firmware encrypts the whole session (see
/// <see cref="ZyxelDalCrypto"/>); older firmware returns no public key and speaks plain JSON.
/// The session cookie and AES key are kept per modem and renewed when the router refuses them.
/// Parsing lives in <see cref="ZyxelCellwanParser"/>.
/// </summary>
public sealed class ZyxelCpeProvider : ICellularModemProvider, IDisposable
{
    /// <inheritdoc/>
    public string ProviderKey => "zyxel-cpe";

    /// <inheritdoc/>
    public string DisplayName => "Zyxel 5G/LTE CPE (HTTPS)";

    /// <summary>The web UI's default admin account, used when no username is saved.</summary>
    public const string DefaultUsername = "admin";

    private const int DefaultTimeoutSeconds = 15;

    private readonly ILogger<ZyxelCpeProvider> _logger;
    private readonly HttpClient _client;
    private readonly ConcurrentDictionary<string, ZyxelSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    // The router counts failed sign-ins and locks the account, so a rejected password is not
    // retried by the poll loop. Only changed credentials or a Probe try again.
    private readonly ConcurrentDictionary<string, string> _rejectedCredentials = new(StringComparer.OrdinalIgnoreCase);

    public ZyxelCpeProvider(ILogger<ZyxelCpeProvider> logger)
        : this(logger, new HttpClientHandler
        {
            // The router serves a self-signed certificate.
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            // Cookies are tracked per modem session, not per client.
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        })
    {
    }

    /// <summary>Test seam: supply the transport.</summary>
    internal ZyxelCpeProvider(ILogger<ZyxelCpeProvider> logger, HttpMessageHandler handler)
    {
        _logger = logger;
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds),
        };
    }

    /// <inheritdoc/>
    public async Task<PollResult<CellularModemStats>> PollAsync(
        ModemPollContext context,
        CancellationToken cancellationToken = default)
    {
        var (stats, failure) = await FetchStatsAsync(context, isProbe: false, cancellationToken);
        return stats != null
            ? PollResult<CellularModemStats>.Ok(stats)
            : PollResult<CellularModemStats>.Failed(failure!);
    }

    /// <inheritdoc/>
    public async Task<(bool success, string message)> TestConnectionAsync(
        ModemPollContext context,
        CancellationToken cancellationToken = default)
    {
        var (stats, failure) = await FetchStatsAsync(context, isProbe: true, cancellationToken);
        if (stats == null)
            return (false, failure!);

        var carrier = string.IsNullOrEmpty(stats.Carrier) ? "" : $" on {stats.Carrier}";
        return (true, $"Detected Zyxel {stats.ModemModel}{carrier}");
    }

    /// <summary>
    /// Sign in if needed, read cellwan_status (and the device info once per session), and
    /// parse it. A refused session is renewed once per call.
    /// </summary>
    private async Task<(CellularModemStats? stats, string? failure)> FetchStatsAsync(
        ModemPollContext context,
        bool isProbe,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
            return (null, "No address is configured for this modem.");

        var host = context.ConfiguredHost ?? context.Host;
        if (string.IsNullOrEmpty(context.Password))
            return (null, "An admin password is required. The router serves signal data only to a signed-in session.");

        var username = string.IsNullOrWhiteSpace(context.Username) ? DefaultUsername : context.Username!;
        var credentials = $"{username}\n{context.Password}";

        // A live session is kept even for a probe: the router can refuse a second sign-in for the
        // same account ("Duplicated login"). Changed credentials still sign in fresh.
        if (_sessions.TryGetValue(context.CacheKey, out var existing) &&
            !string.Equals(existing.Credentials, credentials, StringComparison.Ordinal))
            _sessions.TryRemove(context.CacheKey, out _);

        if (isProbe)
        {
            _rejectedCredentials.TryRemove(context.CacheKey, out _);
        }
        else if (_rejectedCredentials.TryGetValue(context.CacheKey, out var rejected) &&
                 string.Equals(rejected, credentials, StringComparison.Ordinal))
        {
            return (null, RejectedMessage(host));
        }

        try
        {
            var baseUrl = BuildBaseUrl(context);

            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (!_sessions.TryGetValue(context.CacheKey, out var session))
                {
                    var login = await LoginAsync(baseUrl, username, context.Password, credentials, host, cancellationToken);
                    if (login.Session == null)
                    {
                        if (login.Rejected)
                        {
                            _rejectedCredentials[context.CacheKey] = credentials;
                            _logger.LogWarning("Zyxel router {Host} rejected the admin credentials", host);
                        }
                        return (null, login.Failure);
                    }
                    session = login.Session;
                    _sessions[context.CacheKey] = session;
                }

                var cellwan = await GetDalObjectAsync(baseUrl, session, "cellwan_status", cancellationToken);
                if (cellwan == null)
                {
                    _logger.LogInformation("Zyxel session for {Host} was refused, signing in again", host);
                    _sessions.TryRemove(context.CacheKey, out _);
                    continue;
                }

                if (!session.DeviceInfoRead)
                {
                    // Model and firmware do not change within a session, and "status" is a large
                    // object, so it is read once per sign-in. Its absence is not a poll failure.
                    var status = await GetDalObjectAsync(baseUrl, session, "status", cancellationToken);
                    if (status is { } s && s.TryGetProperty("DeviceInfo", out var info) && info.ValueKind == JsonValueKind.Object)
                        session.DeviceInfo = info.Clone();
                    session.DeviceInfoRead = true;
                }

                return (ZyxelCellwanParser.Parse(cellwan.Value, session.DeviceInfo, context), null);
            }

            return (null, $"{host} signed in, then refused the session. Try Probe & Detect.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Zyxel router {Host} returned a response that is not JSON", host);
            return (null, HttpFailureSummary.ForResponse(null, host));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Error polling Zyxel router {Name} at {Host}", context.Name, host);
            return (null, HttpFailureSummary.Describe(ex, host));
        }
    }

    /// <summary>The outcome of one sign-in: a session, or why there is none.</summary>
    private sealed record LoginResult(ZyxelSession? Session, string? Failure, bool Rejected);

    /// <summary>
    /// Sign in as the web UI does. Encrypts the login when the router offers an RSA key, and
    /// sends it as plain JSON when it does not.
    /// </summary>
    private async Task<LoginResult> LoginAsync(
        string baseUrl, string username, string password, string credentials, string host,
        CancellationToken cancellationToken)
    {
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);

        // Mints the pre-login session cookie on the firmware that uses one.
        using (var info = await SendAsync(HttpMethod.Get, $"{baseUrl}/GetInfoNoLogin", baseUrl, cookies, null, cancellationToken))
            CaptureCookies(info, cookies);

        string? rsaKey = null;
        using (var keyResponse = await SendAsync(HttpMethod.Get, $"{baseUrl}/getRSAPublickKey", baseUrl, cookies, null, cancellationToken))
        {
            CaptureCookies(keyResponse, cookies);
            if (keyResponse.IsSuccessStatusCode)
                rsaKey = ReadRsaKey(await keyResponse.Content.ReadAsStringAsync(cancellationToken));
        }

        var loginJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Input_Account"] = username,
            ["Input_Passwd"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(password)),
            ["currLang"] = "en",
            ["RememberPassword"] = 0,
        });

        byte[]? aesKey = null;
        string body;
        if (rsaKey != null)
        {
            aesKey = ZyxelDalCrypto.NewAesKey();
            body = ZyxelDalCrypto.EncryptLogin(loginJson, aesKey, rsaKey);
        }
        else
        {
            body = loginJson;
        }

        using var response = await SendAsync(HttpMethod.Post, $"{baseUrl}/UserLogin", baseUrl, cookies, body, cancellationToken);
        CaptureCookies(response, cookies);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new LoginResult(null, RejectedMessage(host), Rejected: true);
        response.EnsureSuccessStatusCode();

        using var doc = await ReadBodyAsync(response, aesKey, cancellationToken);
        var root = doc.RootElement;
        var result = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        var sessionKey = root.TryGetProperty("sessionkey", out var k) ? ReadScalar(k) : null;

        if (result == ZyxelCellwanParser.SuccessResult || (result == null && sessionKey != null))
            return new LoginResult(new ZyxelSession(cookies, aesKey, credentials) { SessionKey = sessionKey }, null, false);

        return result switch
        {
            "Invalid Username or Password" => new LoginResult(null, RejectedMessage(host), Rejected: true),
            "Locked User" => new LoginResult(null,
                $"{host} has temporarily locked the admin account after failed sign-ins.", false),
            "Duplicated login" => new LoginResult(null,
                $"{host} refused the sign-in because the account is signed in elsewhere. Sign out of its web interface, then try again.", false),
            "Maxium number of login account has reached" or "Maximum number of login account has reached" =>
                new LoginResult(null,
                    $"{host} has reached its limit of signed-in sessions. Sign out of its web interface or wait for idle sessions to time out.", false),
            _ => new LoginResult(null, $"{host} refused the sign-in ({result ?? "no reason given"}).", false),
        };
    }

    /// <summary>
    /// Read one DAL object. Null when the router refused the session: an auth status, a body
    /// the session key cannot decrypt, or a result other than success.
    /// </summary>
    private async Task<JsonElement?> GetDalObjectAsync(
        string baseUrl, ZyxelSession session, string oid, CancellationToken cancellationToken)
    {
        var url = $"{baseUrl}/cgi-bin/DAL?oid={oid}";
        if (session.SessionKey != null)
            url += "&sessionkey=" + Uri.EscapeDataString(session.SessionKey);

        using var response = await SendAsync(HttpMethod.Get, url, baseUrl, session.Cookies, null, cancellationToken);
        CaptureCookies(response, session.Cookies);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return null;
        response.EnsureSuccessStatusCode();

        JsonDocument doc;
        try
        {
            doc = await ReadBodyAsync(response, session.AesKey, cancellationToken);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException ||
                                   (ex is JsonException && session.AesKey != null))
        {
            // Garbage after decryption means the router has moved to a different key.
            _logger.LogDebug(ex, "Zyxel DAL {Oid} did not decrypt with the session key", oid);
            return null;
        }

        using (doc)
        {
            if (doc.RootElement.TryGetProperty("sessionkey", out var k) && ReadScalar(k) is { } newKey)
                session.SessionKey = newKey;

            return ZyxelCellwanParser.TryGetFirstObject(doc.RootElement, out var obj) ? obj.Clone() : null;
        }
    }

    /// <summary>Parse a response body, decrypting it when it is an envelope.</summary>
    private static async Task<JsonDocument> ReadBodyAsync(
        HttpResponseMessage response, byte[]? aesKey, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var doc = JsonDocument.Parse(text);
        if (aesKey == null || !ZyxelDalCrypto.IsEnvelope(doc.RootElement))
            return doc;

        using (doc)
            return JsonDocument.Parse(ZyxelDalCrypto.Decrypt(doc.RootElement, aesKey));
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, string baseUrl, Dictionary<string, string> cookies, string? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.TryAddWithoutValidation("If-Modified-Since", "Thu, 01 Jun 1970 00:00:00 GMT");
        request.Headers.TryAddWithoutValidation("Origin", baseUrl);
        request.Headers.TryAddWithoutValidation("Referer", baseUrl + "/");
        if (cookies.Count > 0)
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies.Select(c => $"{c.Key}={c.Value}")));
        if (body != null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

        return await _client.SendAsync(request, cancellationToken);
    }

    private static void CaptureCookies(HttpResponseMessage response, Dictionary<string, string> cookies)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
            return;

        foreach (var value in values)
        {
            var pair = value.Split(';', 2)[0].Trim();
            var eq = pair.IndexOf('=');
            if (eq > 0)
                cookies[pair[..eq]] = pair[(eq + 1)..];
        }
    }

    /// <summary>The RSA key from /getRSAPublickKey, or null when the firmware has none ("None" counts as none).</summary>
    private static string? ReadRsaKey(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("RSAPublicKey", out var key) &&
                key.ValueKind == JsonValueKind.String &&
                key.GetString() is { Length: > 0 } s &&
                !string.Equals(s, "None", StringComparison.OrdinalIgnoreCase))
                return s;
        }
        catch (JsonException)
        {
            // No key endpoint on this firmware: the session is plain JSON.
        }
        return null;
    }

    private static string? ReadScalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() is { Length: > 0 } s ? s : null,
        JsonValueKind.Number => e.GetRawText(),
        _ => null,
    };

    private static string BuildBaseUrl(ModemPollContext context)
    {
        var port = context.Port > 0 ? context.Port : 443;
        var scheme = port == 80 ? "http" : "https";
        var suffix = port is 80 or 443 ? "" : $":{port}";
        return $"{scheme}://{context.Host}{suffix}";
    }

    private static string RejectedMessage(string host) =>
        $"{host} rejected the admin username or password. Polling pauses until the credentials change or Probe & Detect runs, so the router does not lock the account.";

    public void Dispose() => _client.Dispose();

    /// <summary>
    /// One signed-in session: its cookies, AES key (null on plain firmware), DAL session key, and
    /// the credentials it was opened with.
    /// </summary>
    private sealed class ZyxelSession(Dictionary<string, string> cookies, byte[]? aesKey, string credentials)
    {
        public Dictionary<string, string> Cookies { get; } = cookies;
        public byte[]? AesKey { get; } = aesKey;
        public string Credentials { get; } = credentials;
        public string? SessionKey { get; set; }
        public bool DeviceInfoRead { get; set; }
        public JsonElement? DeviceInfo { get; set; }
    }
}
