using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;

namespace NetworkOptimizer.Web.Services.CableModemProviders;

/// <summary>
/// Cable modem provider for ARRIS Surfboard modems that use HNAP-over-HTTPS
/// (S33/S34 firmware families). Supports both SHA256 and MD5 HNAP login digests.
/// </summary>
public sealed class ArrisSurfboardHnapProvider : ICableModemProvider, IDisposable
{
    /// <inheritdoc/>
    public string ProviderKey => "arris-surfboard-hnap";

    /// <inheritdoc/>
    public string DisplayName => "ARRIS Surfboard S33/S34 (HNAP)";

    private const string StatusPath = "/Cmconnectionstatus.html";
    private const string HnapPath = "/HNAP1/";
    private const string SoapActionPrefix = "http://purenetworks.com/HNAP1/";
    private const string RowDelimiter = "|+|";
    private const char ColumnDelimiter = '^';
    private const int TimeoutSeconds = 15;

    private readonly ILogger<ArrisSurfboardHnapProvider> _logger;
    private readonly ConcurrentDictionary<int, HnapSession> _sessions = new();
    private readonly ConditionalWeakTable<HttpClient, CookieContainer> _cookieContainers = new();

    public ArrisSurfboardHnapProvider(ILogger<ArrisSurfboardHnapProvider> logger)
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
            _logger.LogWarning("ARRIS Surfboard HNAP poll requested but Host is empty (config {Id})", context.Id);
            return null;
        }

        try
        {
            var stats = await TryHnapAsync(context, cancellationToken);
            if (stats != null)
            {
                _logger.LogDebug(
                    "ARRIS Surfboard HNAP {Name} polled: {Model}, {DsCount} DS channels, {UsCount} US channels",
                    context.Name, stats.DeviceModel,
                    stats.DownstreamChannels.Count, stats.UpstreamChannels.Count);
            }

            return stats;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _sessions.TryRemove(context.Id, out _);
            _logger.LogWarning(ex, "Error polling ARRIS Surfboard HNAP {Name} at {Host}", context.Name, context.ConfiguredHost ?? context.Host);
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
            var stats = await TryHnapAsync(context, cancellationToken);
            if (stats != null)
            {
                return (true, $"Connected to {stats.DeviceModel} (HNAP) - " +
                    $"{stats.DownstreamChannels.Count} downstream, {stats.UpstreamChannels.Count} upstream channels detected");
            }

            return (false, "Could not connect via ARRIS HNAP. Check host and credentials.");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    private async Task<CableModemStats?> TryHnapAsync(CmPollContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.Password))
            return null;

        using var client = CreateHttpClient();
        var baseUrl = BuildBaseUrl(context);
        var endpoint = baseUrl + HnapPath;

        try
        {
            var session = await EnsureSessionAsync(client, endpoint, context, cancellationToken);
            if (session == null)
                return null;

            using var deviceResponse = await CallMultipleHnapsAsync(
                client,
                endpoint,
                session,
                ["GetArrisDeviceStatus"],
                cancellationToken);

            using var channelResponse = await CallMultipleHnapsAsync(
                client,
                endpoint,
                session,
                ["GetArrisDeviceStatus", "GetCustomerStatusDownstreamChannelInfo", "GetCustomerStatusUpstreamChannelInfo"],
                cancellationToken);

            if (channelResponse == null)
            {
                _sessions.TryRemove(context.Id, out _);
                return null;
            }

            return ParseHnap(deviceResponse, channelResponse, context);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _sessions.TryRemove(context.Id, out _);
            _logger.LogDebug(ex, "ARRIS Surfboard HNAP request failed for {Name} at {Host}", context.Name, context.ConfiguredHost ?? context.Host);
            return null;
        }
    }

    private async Task<HnapSession?> EnsureSessionAsync(
        HttpClient client,
        string endpoint,
        CmPollContext context,
        CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(context.Id, out var cached))
        {
            ApplySession(client, endpoint[..^HnapPath.Length], cached);

            using var testResponse = await CallMultipleHnapsAsync(
                client,
                endpoint,
                cached,
                ["GetArrisDeviceStatus"],
                cancellationToken);

            if (testResponse != null)
                return cached;

            _sessions.TryRemove(context.Id, out _);
        }

        var session = await LoginAsync(client, endpoint, context, cancellationToken);
        if (session != null)
            _sessions[context.Id] = session;

        return session;
    }

    private async Task<HnapSession?> LoginAsync(
        HttpClient client,
        string endpoint,
        CmPollContext context,
        CancellationToken cancellationToken)
    {
        var username = string.IsNullOrWhiteSpace(context.Username) ? "admin" : context.Username;
        var password = context.Password ?? "";

        await SeedSessionAsync(client, endpoint[..^HnapPath.Length], cancellationToken);
        await ProbeDeviceStatusAsync(client, endpoint, cancellationToken);

        var requestPayload = new Dictionary<string, object>
        {
            ["Login"] = new Dictionary<string, string>
            {
                ["Action"] = "request",
                ["Username"] = username,
                ["LoginPassword"] = "",
                ["Captcha"] = "",
                ["PrivateLogin"] = "LoginPassword",
            },
        };

        using var phase1Response = await PostHnapAsync(
            client, endpoint, "Login", "withoutloginkey", requestPayload, cancellationToken);

        if (phase1Response == null || !phase1Response.RootElement.TryGetProperty("LoginResponse", out var loginResp))
            return null;

        if (!loginResp.TryGetProperty("PublicKey", out var publicKeyEl) ||
            !loginResp.TryGetProperty("Challenge", out var challengeEl) ||
            !loginResp.TryGetProperty("Cookie", out var cookieEl))
        {
            return null;
        }

        var publicKey = publicKeyEl.GetString() ?? "";
        var challenge = challengeEl.GetString() ?? "";
        var uid = cookieEl.GetString();

        foreach (var digest in new[] { HnapDigest.Sha256, HnapDigest.Md5 })
        {
            var privateKey = HmacHex(digest, publicKey + password, challenge);
            var loginPassword = HmacHex(digest, privateKey, challenge);
            var session = new HnapSession(privateKey, uid, digest);
            ApplySession(client, endpoint[..^HnapPath.Length], session);

            var loginPayload = new Dictionary<string, object>
            {
                ["Login"] = new Dictionary<string, string>
                {
                    ["Action"] = "login",
                    ["Username"] = username,
                    ["LoginPassword"] = loginPassword,
                    ["Captcha"] = "",
                    ["PrivateLogin"] = "LoginPassword",
                },
            };

            using var phase2Response = await PostHnapAsync(
                client, endpoint, "Login", privateKey, loginPayload, cancellationToken, digest: digest);

            if (phase2Response == null || !phase2Response.RootElement.TryGetProperty("LoginResponse", out var authResp))
                continue;

            var result = authResp.TryGetProperty("LoginResult", out var resultEl) ? resultEl.GetString() : null;
            if (string.Equals(result, "OK", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(result, "OK_CHANGED", StringComparison.OrdinalIgnoreCase))
            {
                return session;
            }
        }

        return null;
    }

    private async Task ProbeDeviceStatusAsync(HttpClient client, string endpoint, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object>
        {
            ["GetMultipleHNAPs"] = new Dictionary<string, string>
            {
                ["GetArrisDeviceStatus"] = "",
            },
        };

        using var response = await PostHnapAsync(
            client,
            endpoint,
            "GetMultipleHNAPs",
            "withoutloginkey",
            payload,
            cancellationToken);
    }

    private async Task SeedSessionAsync(HttpClient client, string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            await client.GetAsync(baseUrl + "/Login.html", cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "ARRIS Surfboard HNAP login page seed failed");
        }
    }

    private async Task<JsonDocument?> CallMultipleHnapsAsync(
        HttpClient client,
        string endpoint,
        HnapSession session,
        string[] actions,
        CancellationToken cancellationToken)
    {
        var innerDict = new Dictionary<string, string>();
        foreach (var action in actions)
            innerDict[action] = "";

        var payload = new Dictionary<string, object>
        {
            ["GetMultipleHNAPs"] = innerDict,
        };

        var response = await PostHnapAsync(
            client, endpoint, "GetMultipleHNAPs", session.PrivateKey, payload, cancellationToken, StatusPath, session.Digest);

        if (response == null)
            return null;

        if (response.RootElement.TryGetProperty("GetMultipleHNAPsResponse", out var multiResp) &&
            multiResp.TryGetProperty("GetMultipleHNAPsResult", out var result) &&
            string.Equals(result.GetString(), "OK", StringComparison.OrdinalIgnoreCase))
        {
            return response;
        }

        response.Dispose();
        return null;
    }

    private async Task<JsonDocument?> PostHnapAsync(
        HttpClient client,
        string endpoint,
        string action,
        string privateKey,
        object payload,
        CancellationToken cancellationToken,
        string refererPath = "/Login.html",
        HnapDigest digest = HnapDigest.Sha256)
    {
        var hnapAuth = MakeHnapAuth(action, privateKey, digest);
        var soapAction = $"\"{SoapActionPrefix}{action}\"";

        using var content = CreateHnapContent(action, payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = content;
        request.Headers.TryAddWithoutValidation("HNAP_AUTH", hnapAuth);
        request.Headers.TryAddWithoutValidation("SOAPAction", soapAction);
        request.Headers.Referrer = new Uri(endpoint[..^HnapPath.Length] + refererPath);
        if (string.Equals(action, "Login", StringComparison.OrdinalIgnoreCase))
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("ARRIS Surfboard HNAP POST {Action} returned {Status}", action, response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonDocument.Parse(body);
        }
        catch (HttpRequestException ex)
        {
            if (string.Equals(action, "GetMultipleHNAPs", StringComparison.OrdinalIgnoreCase) &&
                ex.Message.Contains("invalid header name", StringComparison.OrdinalIgnoreCase))
            {
                var fallback = await PostHnapRawForMalformedHeadersAsync(
                    client,
                    endpoint,
                    action,
                    privateKey,
                    payload,
                    refererPath,
                    digest,
                    cancellationToken);
                if (fallback != null)
                    return fallback;
            }

            _logger.LogDebug(ex, "ARRIS Surfboard HNAP POST {Action} failed", action);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "ARRIS Surfboard HNAP POST {Action} returned invalid JSON", action);
            return null;
        }
    }

    private async Task<JsonDocument?> PostHnapRawForMalformedHeadersAsync(
        HttpClient client,
        string endpoint,
        string action,
        string privateKey,
        object payload,
        string refererPath,
        HnapDigest digest,
        CancellationToken cancellationToken)
    {
        var endpointUri = new Uri(endpoint);
        var baseUrl = endpoint[..^HnapPath.Length];
        var json = JsonSerializer.Serialize(payload);
        var bodyBytes = Encoding.UTF8.GetBytes(json);
        var soapAction = $"\"{SoapActionPrefix}{action}\"";
        var hnapAuth = MakeHnapAuth(action, privateKey, digest);
        var cookieHeader = "";
        if (_cookieContainers.TryGetValue(client, out var cookies))
            cookieHeader = cookies.GetCookieHeader(new Uri(baseUrl));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        var rawToken = timeoutCts.Token;

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(endpointUri.Host, endpointUri.Port, rawToken);
            await using var stream = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await stream.AuthenticateAsClientAsync(endpointUri.Host);

            var requestBuilder = new StringBuilder()
                .Append("POST ").Append(endpointUri.PathAndQuery).Append(" HTTP/1.1\r\n")
                .Append("Host: ").Append(endpointUri.Host).Append("\r\n")
                .Append("User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36\r\n")
                .Append("Accept: application/json\r\n")
                .Append("Content-Type: application/json\r\n")
                .Append("SOAPAction: ").Append(soapAction).Append("\r\n")
                .Append("HNAP_AUTH: ").Append(hnapAuth).Append("\r\n")
                .Append("Referer: ").Append(baseUrl).Append(refererPath).Append("\r\n")
                .Append("Content-Length: ").Append(bodyBytes.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
                .Append("Connection: close\r\n");

            if (!string.IsNullOrWhiteSpace(cookieHeader))
                requestBuilder.Append("Cookie: ").Append(cookieHeader).Append("\r\n");

            requestBuilder.Append("\r\n");
            var headerBytes = Encoding.ASCII.GetBytes(requestBuilder.ToString());
            await stream.WriteAsync(headerBytes, rawToken);
            await stream.WriteAsync(bodyBytes, rawToken);
            await stream.FlushAsync(rawToken);

            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, rawToken);
                if (read == 0)
                    break;
                memory.Write(buffer, 0, read);
            }

            var responseText = Encoding.UTF8.GetString(memory.ToArray());
            return ParseRawHnapResponse(responseText);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("ARRIS Surfboard HNAP malformed-header fallback POST {Action} timed out", action);
            return null;
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or JsonException)
        {
            _logger.LogDebug(ex, "ARRIS Surfboard HNAP malformed-header fallback POST {Action} failed", action);
            return null;
        }
    }

    private static HttpContent CreateHnapContent(string action, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        if (string.Equals(action, "Login", StringComparison.OrdinalIgnoreCase))
            return new StringContent(json, Encoding.UTF8, "application/json");

        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    internal static JsonDocument? ParseRawHnapResponse(string responseText)
    {
        var jsonStart = responseText.IndexOf('{');
        var jsonEnd = responseText.LastIndexOf('}');
        if (jsonStart < 0 || jsonEnd <= jsonStart)
            return null;

        return JsonDocument.Parse(responseText[jsonStart..(jsonEnd + 1)]);
    }

    internal static CableModemStats ParseHnap(JsonDocument? deviceResponse, JsonDocument channelResponse, CmPollContext context)
    {
        var stats = new CableModemStats
        {
            Timestamp = DateTime.UtcNow,
            DeviceHost = context.ConfiguredHost ?? context.Host,
            DeviceName = context.Name,
            DeviceModel = "ARRIS Surfboard HNAP",
        };

        ApplyDeviceModel(stats, deviceResponse);
        ApplyDeviceModel(stats, channelResponse);

        if (!channelResponse.RootElement.TryGetProperty("GetMultipleHNAPsResponse", out var multiResp))
            return stats;

        if (multiResp.TryGetProperty("GetCustomerStatusDownstreamChannelInfoResponse", out var dsResp) &&
            dsResp.TryGetProperty("CustomerConnDownstreamChannel", out var dsData))
        {
            ParseDownstreamRows(dsData.GetString(), stats);
        }

        if (multiResp.TryGetProperty("GetCustomerStatusUpstreamChannelInfoResponse", out var usResp) &&
            usResp.TryGetProperty("CustomerConnUpstreamChannel", out var usData))
        {
            ParseUpstreamRows(usData.GetString(), stats);
        }

        return stats;
    }

    private static void ApplyDeviceModel(CableModemStats stats, JsonDocument? response)
    {
        if (response == null ||
            !response.RootElement.TryGetProperty("GetMultipleHNAPsResponse", out var multiResp) ||
            !multiResp.TryGetProperty("GetArrisDeviceStatusResponse", out var deviceStatus) ||
            !deviceStatus.TryGetProperty("StatusSoftwareModelName", out var modelEl))
        {
            return;
        }

        var model = modelEl.GetString();
        if (!string.IsNullOrWhiteSpace(model))
            stats.DeviceModel = model.StartsWith("ARRIS", StringComparison.OrdinalIgnoreCase) ? model : $"ARRIS {model}";
    }

    internal static void ParseDownstreamRows(string? tableStr, CableModemStats stats)
    {
        if (string.IsNullOrWhiteSpace(tableStr))
            return;

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

    internal static void ParseUpstreamRows(string? tableStr, CableModemStats stats)
    {
        if (string.IsNullOrWhiteSpace(tableStr))
            return;

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

    private HttpClient CreateHttpClient()
    {
        var cookies = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            UseCookies = true,
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
            DefaultRequestHeaders =
            {
                { "Accept", "application/json" },
                { "Cache-Control", "no-cache" },
                { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36" },
            },
        };

        _cookieContainers.Add(client, cookies);
        return client;
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

    private static long ParseFrequency(string text)
    {
        var cleaned = StripUnits(text);
        return long.TryParse(cleaned, out var val) ? val : 0;
    }

    private static string StripUnits(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var cleaned = text.Trim();

        string[] units = { "Ksym/sec", "Msym/sec", "dBmV", "dB", "MHz", "Hz" };
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

    private static string BuildBaseUrl(CmPollContext context)
    {
        var port = context.Port is > 0 and not 80 ? context.Port : 443;
        var portSuffix = port == 443 ? "" : $":{port}";
        return $"https://{context.Host}{portSuffix}";
    }

    private static string MakeHnapAuth(string action, string privateKey, HnapDigest digest)
    {
        var timestamp = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 2000000000000).ToString(CultureInfo.InvariantCulture);
        var soapActionUri = $"\"{SoapActionPrefix}{action}\"";
        var hash = HmacHex(digest, privateKey, timestamp + soapActionUri);
        return $"{hash} {timestamp}";
    }

    internal static string HmacMd5Hex(string key, string message)
        => HmacHex(HnapDigest.Md5, key, message);

    private static string HmacHex(HnapDigest digest, string key, string message)
    {
        using HMAC hmac = digest == HnapDigest.Md5
            ? new HMACMD5(Encoding.UTF8.GetBytes(key))
            : new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(message)));
    }

    private void ApplySession(HttpClient client, string baseUrl, HnapSession session)
    {
        client.DefaultRequestHeaders.Remove("Cookie");

        if (!_cookieContainers.TryGetValue(client, out var cookies))
            return;

        var uri = new Uri(baseUrl);
        if (!string.IsNullOrWhiteSpace(session.Uid))
            cookies.Add(uri, new Cookie("uid", session.Uid, "/"));
        cookies.Add(uri, new Cookie("PrivateKey", session.PrivateKey, "/"));
    }

    public void Dispose()
    {
        _sessions.Clear();
    }

    internal enum HnapDigest
    {
        Sha256,
        Md5,
    }

    private sealed record HnapSession(string PrivateKey, string? Uid, HnapDigest Digest);
}
