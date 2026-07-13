using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;

namespace NetworkOptimizer.Web.Services.CableModemProviders;

/// <summary>
/// Cable modem provider for Motorola DOCSIS modems that use the HNAP protocol
/// (MB8611, MB8600, MB7621, etc.). Communicates via JSON to the /HNAP1/ endpoint
/// with HMAC-MD5 authentication. The transport scheme is detected on first contact
/// and cached per configuration: HTTPS is tried first so modems already working over
/// TLS are unaffected, with HTTP as a fallback for modems whose HTTPS handshake .NET
/// cannot complete.
/// </summary>
public sealed class MotorolaHnapProvider : ICableModemProvider, IDisposable
{
    /// <inheritdoc/>
    public string ProviderKey => "motorola-hnap";

    /// <inheritdoc/>
    public string DisplayName => "Motorola (HNAP)";

    private const string HnapPath = "/HNAP1/";
    private const string SoapActionPrefix = "http://purenetworks.com/HNAP1/";
    private const string RowDelimiter = "|+|";
    private const char ColumnDelimiter = '^';
    private const int TimeoutSeconds = 15;

    /// <summary>
    /// How long to suspend automatic login retries after a failed login. Newer Motorola
    /// firmware (e.g. 8611-19.2.x, 8600-19.3.x) bans the client after 3 failed attempts
    /// by silently dropping packets for 5 minutes, so retrying on every poll would keep
    /// the lockout tripped forever and every request would surface as a timeout.
    /// </summary>
    private static readonly TimeSpan LoginFailureBackoff = TimeSpan.FromMinutes(5);

    private readonly ILogger<MotorolaHnapProvider> _logger;

    /// <summary>
    /// Cached HNAP sessions keyed by CmConfiguration.Id.
    /// Each session holds the derived private key and cookies for reuse across polls.
    /// </summary>
    private readonly ConcurrentDictionary<int, HnapSession> _sessions = new();

    /// <summary>
    /// Per-config timestamps until which automatic login attempts are suspended
    /// after a login failure, to avoid tripping the modem's 3-strike lockout.
    /// </summary>
    private readonly ConcurrentDictionary<int, DateTime> _loginBackoffUntil = new();

    /// <summary>
    /// Per-config detected transport scheme: true = HTTPS, false = HTTP. Determined on
    /// first successful contact (a Test press or the first poll) and reused so later
    /// polls skip protocol detection. Re-detected after a process restart.
    /// </summary>
    private readonly ConcurrentDictionary<int, bool> _useHttps = new();

    public MotorolaHnapProvider(ILogger<MotorolaHnapProvider> logger)
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
            _logger.LogWarning("Motorola HNAP poll requested but Host is empty (config {Id})", context.Id);
            return null;
        }

        if (_loginBackoffUntil.TryGetValue(context.Id, out var backoffUntil))
        {
            if (DateTime.UtcNow < backoffUntil)
            {
                _logger.LogDebug(
                    "Motorola HNAP {Name}: skipping poll until {Until:HH:mm:ss} UTC after login failure",
                    context.Name, backoffUntil);
                return null;
            }

            _loginBackoffUntil.TryRemove(context.Id, out _);
        }

        try
        {
            using var client = CreateHttpClient();

            var sessionInfo = await EnsureSessionAsync(client, context, cancellationToken);
            if (sessionInfo == null)
            {
                _logger.LogWarning("Motorola HNAP {Name} at {Host}: login failed", context.Name, context.ConfiguredHost ?? context.Host);
                return null;
            }

            var (session, baseUrl) = sessionInfo.Value;

            using var response = await CallMultipleHnapsAsync(
                client, baseUrl, session,
                ["GetMotoStatusDownstreamChannelInfo",
                 "GetMotoStatusUpstreamChannelInfo",
                 "GetMotoStatusConnectionInfo",
                 "GetMotoStatusSoftware"],
                cancellationToken);

            if (response == null)
            {
                _sessions.TryRemove(context.Id, out _);
                _logger.LogWarning("Motorola HNAP {Name} at {Host}: GetMultipleHNAPs failed", context.Name, context.ConfiguredHost ?? context.Host);
                return null;
            }

            var stats = ParseResponse(response, context);

            _logger.LogDebug(
                "Motorola HNAP {Name} polled: {DsCount} DS channels, {UsCount} US channels",
                context.Name, stats.DownstreamChannels.Count, stats.UpstreamChannels.Count);

            return stats;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _sessions.TryRemove(context.Id, out _);
            _logger.LogWarning(ex, "Error polling Motorola HNAP {Name} at {Host}", context.Name, context.ConfiguredHost ?? context.Host);
            return null;
        }
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
            using var client = CreateHttpClient();

            var sessionInfo = await EnsureSessionAsync(client, context, cancellationToken);
            if (sessionInfo == null)
                return (false, "Could not log in over HTTP or HTTPS. Check the host and credentials; " +
                    "if there have been several failed logins, wait 5 minutes for the modem's lockout to clear.");

            var (session, baseUrl) = sessionInfo.Value;

            using var response = await CallMultipleHnapsAsync(
                client, baseUrl, session,
                ["GetMotoStatusDownstreamChannelInfo",
                 "GetMotoStatusUpstreamChannelInfo"],
                cancellationToken);

            if (response == null)
                return (false, "Connected but could not retrieve channel data.");

            var dsCount = CountChannels(response, "GetMotoStatusDownstreamChannelInfoResponse", "MotoConnDownstreamChannel");
            var usCount = CountChannels(response, "GetMotoStatusUpstreamChannelInfoResponse", "MotoConnUpstreamChannel");

            await LogoutAsync(client, baseUrl, cancellationToken);

            return (true, $"Connected via HNAP - {dsCount} downstream, {usCount} upstream channels detected");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, "Connection timed out. The modem may be in its 5-minute lockout after " +
                "failed login attempts (it drops packets while locked out). Wait 5 minutes and try again.");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolve a working session, detecting (and caching) the HTTP/HTTPS scheme the
    /// modem's HNAP endpoint speaks. Returns the session and the base URL that worked,
    /// or null if no scheme could log in.
    /// </summary>
    private async Task<SessionInfo?> EnsureSessionAsync(
        HttpClient client, CmPollContext context, CancellationToken cancellationToken)
    {
        // Fast path: reuse a cached session over the scheme we already detected.
        if (_useHttps.TryGetValue(context.Id, out var knownScheme) &&
            _sessions.TryGetValue(context.Id, out var cached))
        {
            var cachedUrl = BuildBaseUrl(context, knownScheme);
            using var testResponse = await CallMultipleHnapsAsync(
                client, cachedUrl, cached,
                ["GetMotoStatusConnectionInfo"],
                cancellationToken);

            if (testResponse != null)
                return new SessionInfo(cached, cachedUrl);

            _sessions.TryRemove(context.Id, out _);
            _logger.LogDebug("Motorola HNAP session expired for {Name}, re-authenticating", context.Name);
        }

        // Detect the scheme the modem's HNAP endpoint speaks. HTTPS is tried first so
        // modems already working over TLS keep the exact same path they use today; HTTP
        // is the fallback for modems whose HTTPS handshake .NET can't complete (it stalls
        // until timeout, e.g. on macOS) - those modems also serve /HNAP1/ over plain HTTP.
        foreach (var useHttps in SchemesToTry(context.Id))
        {
            var baseUrl = BuildBaseUrl(context, useHttps);

            HnapSession? session;
            try
            {
                session = await LoginAsync(client, baseUrl, context, cancellationToken);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Request stalled on this scheme (e.g. an HTTPS handshake .NET can't
                // finish); move on and try the next candidate.
                _logger.LogDebug("Motorola HNAP {Name}: {Scheme} attempt timed out, trying next scheme",
                    context.Name, useHttps ? "HTTPS" : "HTTP");
                continue;
            }

            if (session != null)
            {
                _sessions[context.Id] = session;
                _useHttps[context.Id] = useHttps;
                return new SessionInfo(session, baseUrl);
            }

            // The endpoint answered on this scheme but auth failed (lockout backoff is
            // now set). The scheme is correct, so remember it and stop - retrying the
            // other scheme would just burn another failed login against the lockout.
            if (_loginBackoffUntil.ContainsKey(context.Id))
            {
                _useHttps[context.Id] = useHttps;
                return null;
            }

            // Otherwise this scheme never reached the HNAP endpoint (e.g. an HTTP->HTTPS
            // redirect); fall through and try the next scheme.
        }

        // Nothing worked and no scheme was confirmed - drop any stale cached scheme so
        // the next attempt probes both again.
        _useHttps.TryRemove(context.Id, out _);
        return null;
    }

    /// <summary>
    /// Schemes to attempt for this config: the cached one if known, otherwise HTTPS
    /// first (so modems already working over TLS are unchanged) then HTTP.
    /// </summary>
    private bool[] SchemesToTry(int id) =>
        _useHttps.TryGetValue(id, out var scheme) ? [scheme] : [true, false];

    /// <summary>
    /// Two-phase HNAP login: request challenge, derive keys, authenticate.
    /// </summary>
    private async Task<HnapSession?> LoginAsync(
        HttpClient client, string baseUrl, CmPollContext context, CancellationToken cancellationToken)
    {
        var endpoint = baseUrl + HnapPath;
        var username = context.Username ?? "admin";
        var password = context.Password ?? "";

        // Phase 1 is unauthenticated; clear cookies left from any prior attempt (e.g. a
        // different scheme on this reused client) so the challenge request is sent clean.
        client.DefaultRequestHeaders.Remove("Cookie");

        // Phase 1: request challenge
        var requestPayload = new
        {
            Login = new
            {
                Action = "request",
                Username = username,
                LoginPassword = "",
                Captcha = "",
                PrivateLogin = "LoginPassword"
            }
        };

        using var phase1Response = await PostHnapAsync(
            client, endpoint, "Login", "withoutloginkey", requestPayload, cancellationToken);

        if (phase1Response == null)
            return null;

        if (!phase1Response.RootElement.TryGetProperty("LoginResponse", out var loginResp))
            return null;

        var result = loginResp.GetProperty("LoginResult").GetString();
        if (result == "FAILED")
        {
            _loginBackoffUntil[context.Id] = DateTime.UtcNow + LoginFailureBackoff;
            _logger.LogWarning(
                "Motorola HNAP {Name}: login locked out. Wait 5 minutes before retrying", context.Name);
            return null;
        }

        if (!loginResp.TryGetProperty("PublicKey", out var pubKeyEl) ||
            !loginResp.TryGetProperty("Challenge", out var challengeEl))
            return null;

        var publicKey = pubKeyEl.GetString() ?? "";
        var challenge = challengeEl.GetString() ?? "";

        // Derive private key: HMAC-MD5(publicKey + password, challenge)
        var privateKey = HmacMd5Hex(publicKey + password, challenge);

        // Extract uid cookie if provided
        string? uid = null;
        if (loginResp.TryGetProperty("Cookie", out var cookieEl))
            uid = cookieEl.GetString();

        // Phase 2: authenticate with derived password
        var loginPassword = HmacMd5Hex(privateKey, challenge);

        var authPayload = new
        {
            Login = new
            {
                Action = "login",
                Username = username,
                LoginPassword = loginPassword,
                Captcha = "",
                PrivateLogin = "LoginPassword"
            }
        };

        var session = new HnapSession(privateKey, uid);
        ApplySessionCookies(client, session);

        using var phase2Response = await PostHnapAsync(
            client, endpoint, "Login", privateKey, authPayload, cancellationToken);

        if (phase2Response == null)
            return null;

        if (!phase2Response.RootElement.TryGetProperty("LoginResponse", out var authResp))
            return null;

        // Firmware returns OK on success or OK_CHANGED when a password change is being
        // forced; both grant a valid session. FAILED/AUTH_FAIL count toward the modem's
        // 3-strike lockout, so back off before retrying.
        var authResult = authResp.GetProperty("LoginResult").GetString();
        if (authResult != "OK" && authResult != "OK_CHANGED")
        {
            _loginBackoffUntil[context.Id] = DateTime.UtcNow + LoginFailureBackoff;
            _logger.LogWarning(
                "Motorola HNAP {Name}: login authentication failed (result: {Result}). " +
                "Suspending retries for {Minutes} minutes to avoid the modem's failed-login lockout",
                context.Name, authResult, LoginFailureBackoff.TotalMinutes);
            return null;
        }

        _loginBackoffUntil.TryRemove(context.Id, out _);
        _logger.LogDebug("Motorola HNAP {Name}: logged in successfully", context.Name);
        return session;
    }

    /// <summary>
    /// Call GetMultipleHNAPs with the specified actions.
    /// All Motorola status actions must be called this way.
    /// </summary>
    private async Task<JsonDocument?> CallMultipleHnapsAsync(
        HttpClient client, string baseUrl, HnapSession session,
        string[] actions, CancellationToken cancellationToken)
    {
        var endpoint = baseUrl + HnapPath;

        // Each poll uses a fresh HttpClient, so re-apply the session cookies here
        // rather than relying on the client that performed the login.
        ApplySessionCookies(client, session);

        var innerDict = new Dictionary<string, string>();
        foreach (var action in actions)
            innerDict[action] = "";

        var payload = new Dictionary<string, object>
        {
            ["GetMultipleHNAPs"] = innerDict
        };

        var response = await PostHnapAsync(
            client, endpoint, "GetMultipleHNAPs", session.PrivateKey, payload, cancellationToken);

        if (response == null)
            return null;

        if (response.RootElement.TryGetProperty("GetMultipleHNAPsResponse", out var multiResp) &&
            multiResp.TryGetProperty("GetMultipleHNAPsResult", out var multiResult) &&
            multiResult.GetString() == "OK")
        {
            return response;
        }

        return null;
    }

    private async Task<JsonDocument?> PostHnapAsync(
        HttpClient client, string endpoint, string action, string privateKey,
        object payload, CancellationToken cancellationToken)
    {
        var hnapAuth = MakeHnapAuth(action, privateKey);
        var soapAction = $"\"{SoapActionPrefix}{action}\"";

        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = content;
        request.Headers.TryAddWithoutValidation("HNAP_AUTH", hnapAuth);
        request.Headers.TryAddWithoutValidation("SOAPAction", soapAction);
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

        try
        {
            var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("HNAP POST {Action} returned {Status}", action, response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonDocument.Parse(body);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "HNAP POST {Action} failed", action);
            return null;
        }
        catch (JsonException ex)
        {
            // A non-JSON response (e.g. an HTML error/redirect page from the wrong
            // scheme) means this isn't a working HNAP endpoint; treat it as a failed
            // call so scheme detection falls through to the next candidate.
            _logger.LogDebug(ex, "HNAP POST {Action} returned non-JSON body", action);
            return null;
        }
    }

    private async Task LogoutAsync(HttpClient client, string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            await client.GetAsync($"{baseUrl}/Logout.html", cancellationToken);
        }
        catch
        {
            // Logout is best-effort
        }
    }

    private CableModemStats ParseResponse(JsonDocument response, CmPollContext context)
    {
        var stats = new CableModemStats
        {
            Timestamp = DateTime.UtcNow,
            DeviceHost = context.ConfiguredHost ?? context.Host,
            DeviceName = context.Name,
            DeviceModel = "Motorola",
        };

        var root = response.RootElement;
        if (!root.TryGetProperty("GetMultipleHNAPsResponse", out var multiResp))
            return stats;

        // Parse downstream channels from pipe-delimited table string
        if (multiResp.TryGetProperty("GetMotoStatusDownstreamChannelInfoResponse", out var dsResp) &&
            dsResp.TryGetProperty("MotoConnDownstreamChannel", out var dsData))
        {
            var tableStr = dsData.GetString();
            if (!string.IsNullOrWhiteSpace(tableStr))
                ParseDownstreamTable(tableStr, stats);
        }

        // Parse upstream channels
        if (multiResp.TryGetProperty("GetMotoStatusUpstreamChannelInfoResponse", out var usResp) &&
            usResp.TryGetProperty("MotoConnUpstreamChannel", out var usData))
        {
            var tableStr = usData.GetString();
            if (!string.IsNullOrWhiteSpace(tableStr))
                ParseUpstreamTable(tableStr, stats);
        }

        // Extract software version for model identification
        if (multiResp.TryGetProperty("GetMotoStatusSoftwareResponse", out var swResp) &&
            swResp.TryGetProperty("StatusSoftwareCustomerVer", out var verEl))
        {
            var version = verEl.GetString();
            if (!string.IsNullOrWhiteSpace(version))
            {
                // Version strings like "8611-19.2.18" contain the model number
                if (version.StartsWith("8611", StringComparison.Ordinal))
                    stats.DeviceModel = "Motorola MB8611";
                else if (version.StartsWith("8600", StringComparison.Ordinal))
                    stats.DeviceModel = "Motorola MB8600";
                else
                    stats.DeviceModel = $"Motorola ({version.Split('-')[0]})";
            }
        }

        return stats;
    }

    /// <summary>
    /// Parse downstream table string. Columns: Channel, Lock Status, Modulation,
    /// Channel ID, Freq. (MHz), Pwr (dBmV), SNR (dB), Corrected, Uncorrected.
    /// Rows delimited by |+|, columns by ^.
    /// </summary>
    private void ParseDownstreamTable(string tableStr, CableModemStats stats)
    {
        foreach (var row in tableStr.Split(RowDelimiter))
        {
            var cols = row.Split(ColumnDelimiter);
            if (cols.Length < 9) continue;

            var channel = new DsChannel
            {
                ChannelId = ParseInt(cols[3]),
                LockStatus = cols[1].Trim(),
                Modulation = cols[2].Trim(),
                Frequency = ParseFrequency(cols[4]),
                Power = ParseDouble(cols[5]),
                Snr = ParseDouble(cols[6]),
                Correctables = ParseLong(cols[7]),
                Uncorrectables = ParseLong(cols[8]),
            };

            if (channel.ChannelId > 0 || !string.IsNullOrWhiteSpace(channel.LockStatus))
                stats.DownstreamChannels.Add(channel);
        }
    }

    /// <summary>
    /// Parse upstream table string. Columns: Channel, Lock Status, Channel Type,
    /// Channel ID, Symb. Rate (Ksym/sec), Freq. (MHz), Pwr (dBmV).
    /// </summary>
    private void ParseUpstreamTable(string tableStr, CableModemStats stats)
    {
        foreach (var row in tableStr.Split(RowDelimiter))
        {
            var cols = row.Split(ColumnDelimiter);
            if (cols.Length < 7) continue;

            var channel = new UsChannel
            {
                ChannelId = ParseInt(cols[3]),
                LockStatus = cols[1].Trim(),
                ChannelType = cols[2].Trim(),
                SymbolRate = ParseLong(cols[4]),
                Frequency = ParseFrequency(cols[5]),
                Power = ParseDouble(cols[6]) ?? 0,
            };

            if (channel.ChannelId > 0 || !string.IsNullOrWhiteSpace(channel.LockStatus))
                stats.UpstreamChannels.Add(channel);
        }
    }

    private int CountChannels(JsonDocument response, string responseKey, string channelKey)
    {
        if (response.RootElement.TryGetProperty("GetMultipleHNAPsResponse", out var multiResp) &&
            multiResp.TryGetProperty(responseKey, out var resp) &&
            resp.TryGetProperty(channelKey, out var data))
        {
            var str = data.GetString();
            if (!string.IsNullOrWhiteSpace(str))
                return str.Split(RowDelimiter).Length;
        }
        return 0;
    }

    /// <summary>
    /// Build the base URL for the given scheme. A user-specified non-default port is
    /// honored; otherwise the standard port for the scheme is used (80 for HTTP, 443
    /// for HTTPS). HTTPS is attempted first; HTTP is the fallback used when .NET cannot
    /// complete a modem's HTTPS handshake (it stalls until the request times out, even
    /// though the modem's TLS is otherwise standard and a browser connects fine). The
    /// HTML web UI redirects HTTP to HTTPS, but the /HNAP1/ endpoint answers over HTTP.
    /// </summary>
    private static string BuildBaseUrl(CmPollContext context, bool useHttps)
    {
        if (useHttps)
        {
            var httpsPort = context.Port is > 0 and not 80 ? context.Port : 443;
            var httpsSuffix = httpsPort == 443 ? "" : $":{httpsPort}";
            return $"https://{context.Host}{httpsSuffix}";
        }

        var port = context.Port is > 0 and not 443 ? context.Port : 80;
        var portSuffix = port == 80 ? "" : $":{port}";
        return $"http://{context.Host}{portSuffix}";
    }

    /// <summary>
    /// Compute HNAP_AUTH header: HMAC-MD5(privateKey, timestamp + soapActionUri) in hex, space, timestamp.
    /// </summary>
    private static string MakeHnapAuth(string action, string privateKey)
    {
        var timestamp = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 2000000000000).ToString();
        var soapActionUri = $"\"{SoapActionPrefix}{action}\"";
        var message = timestamp + soapActionUri;

        var hmac = HmacMd5Hex(privateKey, message);
        return $"{hmac} {timestamp}";
    }

    /// <summary>
    /// HMAC-MD5(key, message) → uppercase hex string.
    /// </summary>
    private static string HmacMd5Hex(string key, string message)
    {
        using var hmac = new HMACMD5(Encoding.UTF8.GetBytes(key));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        return Convert.ToHexString(hash);
    }

    private static void ApplySessionCookies(HttpClient client, HnapSession session)
    {
        // The modem expects PrivateKey and uid cookies
        var cookieValues = new List<string>();
        cookieValues.Add($"PrivateKey={session.PrivateKey}");
        if (session.Uid != null)
            cookieValues.Add($"uid={session.Uid}");

        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", string.Join("; ", cookieValues));
    }

    private HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            // Accept the modem's self-signed cert when the HTTPS fallback is used.
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            UseCookies = false,
            // Don't follow redirects: a modem that only serves its UI over HTTPS will
            // 301 an HTTP request, and following it would land on the HTTPS path .NET
            // may not be able to complete. A 301 instead reads as "wrong scheme" so
            // detection moves on to HTTPS.
            AllowAutoRedirect = false,
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
            DefaultRequestHeaders =
            {
                { "Accept", "application/json" },
                { "Connection", "keep-alive" },
                { "Cache-Control", "no-cache" },
            },
        };
    }

    private static int ParseInt(string text)
    {
        var cleaned = StripUnits(text);
        return int.TryParse(cleaned, out var val) ? val : 0;
    }

    private static long ParseLong(string text)
    {
        var cleaned = StripUnits(text);
        return long.TryParse(cleaned, out var val) ? val : 0;
    }

    private static double? ParseDouble(string text)
    {
        var cleaned = StripUnits(text);
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var val) ? val : null;
    }

    /// <summary>
    /// Parse a channel table frequency into Hz. Motorola tables report frequency in MHz
    /// with a decimal point (e.g. "711.0"); some firmware reports raw Hz (e.g. "711000000").
    /// Values below 100,000 are treated as MHz.
    /// </summary>
    private static long ParseFrequency(string text)
    {
        var cleaned = StripUnits(text);
        if (!double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
            return 0;

        return val < 100_000 ? (long)(val * 1_000_000) : (long)val;
    }

    private static string StripUnits(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var cleaned = text.Trim();
        string[] units = ["Ksym/sec", "Msym/sec", "dBmV", "dB", "MHz", "Hz"];
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

    public void Dispose()
    {
        _sessions.Clear();
        _loginBackoffUntil.Clear();
        _useHttps.Clear();
    }

    private sealed record HnapSession(string PrivateKey, string? Uid);

    /// <summary>A logged-in session together with the base URL (scheme) that worked.</summary>
    private readonly record struct SessionInfo(HnapSession Session, string BaseUrl);
}
