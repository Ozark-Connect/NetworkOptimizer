using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;

namespace NetworkOptimizer.Web.Services.CableModemProviders;

/// <summary>
/// Cable modem provider for the ARRIS TG3442DE cable gateway, sold as the
/// "Vodafone Station" in Germany. Authenticates with AES-CCM encrypted
/// credentials and scrapes DOCSIS channel data from JS arrays embedded in
/// /php/status_docsis_data.php.
/// </summary>
public sealed partial class VodafoneStationProvider : ICableModemProvider, IDisposable
{
    /// <inheritdoc/>
    public string ProviderKey => "vodafone-station";

    /// <inheritdoc/>
    public string DisplayName => "Vodafone Station (ARRIS TG3442DE)";

    private const string DefaultDocsisPath = "/php/status_docsis_data.php";
    private const string LoginPath = "/php/ajaxSet_Password.php";
    private const string SessionPath = "/php/ajaxSet_Session.php";
    private const string LogoutPath = "/php/logout.php";
    private const string DeviceStatusPath = "/php/status_status_data.php";
    private const string CredentialScriptPath = "/base_95x.js";

    private const string LoginAad = "loginPassword";
    private const string NonceAad = "nonce";
    private const int PbkdfIterations = 1000;
    private const int KeyLengthBytes = 16;
    private const int TagLengthBytes = 16;
    private const int CsrfNonceLength = 32;
    private const int TimeoutSeconds = 15;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private readonly ILogger<VodafoneStationProvider> _logger;
    private readonly ConcurrentDictionary<int, TgSession> _sessions = new();

    public VodafoneStationProvider(ILogger<VodafoneStationProvider> logger)
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
            _logger.LogWarning("Vodafone Station poll requested but Host is empty (config {Id})", context.Id);
            return null;
        }

        try
        {
            var stats = await TryPollAsync(context, cancellationToken);
            if (stats != null)
            {
                _logger.LogDebug(
                    "Vodafone Station {Name} polled: {Model}, {DsCount} DS channels, {UsCount} US channels",
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
            _logger.LogWarning(ex, "Error polling Vodafone Station {Name} at {Host}", context.Name, context.ConfiguredHost ?? context.Host);
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

        if (string.IsNullOrWhiteSpace(context.Password))
            return (false, "Password is required for the Vodafone Station web interface");

        try
        {
            var stats = await TryPollAsync(context, cancellationToken);
            if (stats != null)
            {
                return (true, $"Connected to {stats.DeviceModel} - " +
                    $"{stats.DownstreamChannels.Count} downstream, {stats.UpstreamChannels.Count} upstream channels detected");
            }

            return (false, "Could not read DOCSIS data from the Vodafone Station. Check host, password, " +
                "and that no other admin session is signed in.");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    private async Task<CableModemStats?> TryPollAsync(CmPollContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.Password))
            return null;

        var session = await EnsureSessionAsync(context, cancellationToken);
        if (session == null)
            return null;

        var html = await TryFetchDocsisAsync(session, context, cancellationToken);
        if (html == null)
        {
            // The modem allows a single active admin session, so another sign-in silently
            // invalidates ours. Re-authenticate once before giving up.
            await InvalidateSessionAsync(context, cancellationToken);
            session = await LoginAsync(context, cancellationToken);
            if (session == null)
                return null;

            html = await TryFetchDocsisAsync(session, context, cancellationToken);
            if (html == null)
                return null;
        }

        return ParseDocsis(html, context, session.DeviceModel);
    }

    private async Task<TgSession?> EnsureSessionAsync(CmPollContext context, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(context.Id, out var cached))
            return cached;

        return await LoginAsync(context, cancellationToken);
    }

    private async Task<TgSession?> LoginAsync(CmPollContext context, CancellationToken cancellationToken)
    {
        var baseUrl = BuildBaseUrl(context);
        var username = string.IsNullOrWhiteSpace(context.Username) ? "admin" : context.Username;
        var password = context.Password ?? "";

        var cookies = new CookieContainer();
        using var client = CreateClient(cookies, baseUrl, csrfNonce: null);

        var loginPage = await client.GetStringAsync(baseUrl + "/", cancellationToken);

        var sessionId = ExtractJsVar(loginPage, "currentSessionId");
        var ivHex = ExtractJsVar(loginPage, "myIv");
        var saltHex = ExtractJsVar(loginPage, "mySalt");

        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(ivHex) || string.IsNullOrEmpty(saltHex))
        {
            _logger.LogWarning(
                "Vodafone Station {Name}: login page is missing session variables (sessionId={HasSession}, myIv={HasIv}, mySalt={HasSalt})",
                context.Name, !string.IsNullOrEmpty(sessionId), !string.IsNullOrEmpty(ivHex), !string.IsNullOrEmpty(saltHex));
            return null;
        }

        if (!TryParseHex(saltHex, out var salt) || !TryParseHex(ivHex, out var iv))
        {
            _logger.LogWarning("Vodafone Station {Name}: mySalt/myIv are not valid hex", context.Name);
            return null;
        }

        if (!IsSupportedNonceSize(iv.Length))
        {
            _logger.LogWarning(
                "Vodafone Station {Name}: myIv is {Length} bytes, which AES-CCM does not accept as a nonce",
                context.Name, iv.Length);
            return null;
        }

        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, PbkdfIterations, HashAlgorithmName.SHA256, KeyLengthBytes);

        var credentials = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["Password"] = password,
            ["Nonce"] = sessionId,
        });

        var encryptData = AesCcmEncryptHex(key, iv, Encoding.UTF8.GetBytes(credentials), LoginAad);

        var loginBody = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["EncryptData"] = encryptData,
            ["Name"] = username,
            ["AuthData"] = LoginAad,
        });

        using var loginContent = new StringContent(loginBody, Encoding.UTF8, "application/json");
        using var loginResponse = await client.PostAsync(baseUrl + LoginPath, loginContent, cancellationToken);
        if (!loginResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Vodafone Station {Name}: login POST returned {Status}", context.Name, loginResponse.StatusCode);
            return null;
        }

        var loginJson = await loginResponse.Content.ReadAsStringAsync(cancellationToken);
        using var parsed = TryParseJson(loginJson);
        if (parsed == null)
        {
            _logger.LogWarning("Vodafone Station {Name}: login response was not JSON", context.Name);
            return null;
        }

        var status = GetJsonString(parsed.RootElement, "p_status") ?? "";
        if (status.Equals("Lockout", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Vodafone Station {Name}: account locked out after repeated failed sign-ins. Wait a few minutes or reboot the modem.",
                context.Name);
            return null;
        }

        if (status.Equals("Fail", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Vodafone Station {Name}: authentication failed (incorrect password)", context.Name);
            return null;
        }

        var encryptedNonce = GetJsonString(parsed.RootElement, "encryptData")
            ?? GetJsonString(parsed.RootElement, "EncryptData");

        string csrfNonce;
        if (!string.IsNullOrEmpty(encryptedNonce))
        {
            var decrypted = AesCcmDecryptText(key, iv, encryptedNonce, NonceAad);
            if (string.IsNullOrEmpty(decrypted))
            {
                _logger.LogWarning("Vodafone Station {Name}: could not decrypt the CSRF nonce", context.Name);
                return null;
            }

            csrfNonce = decrypted.Length > CsrfNonceLength ? decrypted[..CsrfNonceLength] : decrypted;
        }
        else
        {
            csrfNonce = GetJsonString(parsed.RootElement, "nonce") ?? sessionId;
        }

        var session = new TgSession(csrfNonce, cookies, "ARRIS TG3442DE (Vodafone Station)");

        // Later requests need the nonce header, so build a fresh client for the remaining setup.
        using var authedClient = CreateClient(cookies, baseUrl, csrfNonce);

        // Some firmware only marks the session live once this is posted; failure is not fatal.
        try
        {
            using var sessionResponse = await authedClient.PostAsync(baseUrl + SessionPath, content: null, cancellationToken);
            _logger.LogDebug(
                "Vodafone Station {Name}: session init returned {Status}", context.Name, sessionResponse.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Vodafone Station {Name}: session init failed", context.Name);
        }

        await ApplyCredentialCookieAsync(authedClient, cookies, baseUrl, context, cancellationToken);

        session = session with { DeviceModel = await ReadDeviceModelAsync(authedClient, baseUrl, session.DeviceModel, cancellationToken) };
        _sessions[context.Id] = session;

        _logger.LogDebug("Vodafone Station {Name}: authenticated (p_status={Status})", context.Name, status);
        return session;
    }

    /// <summary>
    /// Fetch the DOCSIS page, returning null when the session is no longer accepted so the
    /// caller can re-authenticate. A page without the channel arrays means the modem served
    /// the login page instead, which is the unauthenticated response rather than an error.
    /// </summary>
    private async Task<string?> TryFetchDocsisAsync(
        TgSession session,
        CmPollContext context,
        CancellationToken cancellationToken)
    {
        var baseUrl = BuildBaseUrl(context);
        var path = string.IsNullOrWhiteSpace(context.StatusPagePath) ? DefaultDocsisPath : context.StatusPagePath;

        using var client = CreateClient(session.Cookies, baseUrl, session.CsrfNonce);

        try
        {
            using var response = await client.GetAsync(baseUrl + path, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "Vodafone Station {Name}: DOCSIS page returned {Status}", context.Name, response.StatusCode);
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            return DownstreamArrayRegex().IsMatch(html) || UpstreamArrayRegex().IsMatch(html) ? html : null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Vodafone Station {Name}: DOCSIS page request failed", context.Name);
            return null;
        }
    }

    private async Task InvalidateSessionAsync(CmPollContext context, CancellationToken cancellationToken)
    {
        if (!_sessions.TryRemove(context.Id, out var session))
            return;

        var baseUrl = BuildBaseUrl(context);
        using var client = CreateClient(session.Cookies, baseUrl, session.CsrfNonce);

        try
        {
            using var response = await client.PostAsync(baseUrl + LogoutPath, content: null, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Vodafone Station {Name}: logout failed", context.Name);
        }
    }

    /// <summary>
    /// The firmware expects a "credential" cookie that the browser's login script sets from
    /// base_95x.js. Data requests are rejected without it on some builds.
    /// </summary>
    private async Task ApplyCredentialCookieAsync(
        HttpClient client,
        CookieContainer cookies,
        string baseUrl,
        CmPollContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var script = await client.GetStringAsync(baseUrl + CredentialScriptPath, cancellationToken);
            var match = CredentialCookieRegex().Match(script);
            if (!match.Success)
            {
                _logger.LogDebug("Vodafone Station {Name}: no credential cookie in base_95x.js", context.Name);
                return;
            }

            cookies.Add(new Uri(baseUrl), new Cookie("credential", match.Groups[1].Value, "/"));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Vodafone Station {Name}: could not read base_95x.js", context.Name);
        }
    }

    private static async Task<string> ReadDeviceModelAsync(
        HttpClient client,
        string baseUrl,
        string fallback,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await client.GetStringAsync(baseUrl + DeviceStatusPath, cancellationToken);
            var hardware = ExtractJsVar(status, "js_HWTypeVersion");
            if (string.IsNullOrWhiteSpace(hardware))
                return fallback;

            return hardware.StartsWith("ARRIS", StringComparison.OrdinalIgnoreCase) ? hardware : $"ARRIS {hardware}";
        }
        catch (HttpRequestException)
        {
            return fallback;
        }
    }

    internal static CableModemStats ParseDocsis(string html, CmPollContext context, string deviceModel)
    {
        var stats = new CableModemStats
        {
            Timestamp = DateTime.UtcNow,
            DeviceHost = context.ConfiguredHost ?? context.Host,
            DeviceName = context.Name,
            DeviceModel = deviceModel,
        };

        foreach (var channel in EnumerateChannels(html, DownstreamArrayRegex()))
        {
            var type = GetChannelString(channel, "ChannelType");
            var modulation = GetChannelString(channel, "Modulation");
            if (string.IsNullOrWhiteSpace(modulation) && type.Contains("OFDM", StringComparison.OrdinalIgnoreCase))
                modulation = type;

            stats.DownstreamChannels.Add(new DsChannel
            {
                ChannelId = (int)GetChannelNumber(channel, "ChannelID"),
                LockStatus = NormalizeLockStatus(GetChannelString(channel, "LockStatus")),
                Modulation = modulation,
                Frequency = ParseFrequencyHz(GetChannelString(channel, "Frequency")),
                Power = ParsePowerDbmv(GetChannelString(channel, "PowerLevel")),
                Snr = ParseLevel(GetChannelString(channel, "SNRLevel")),
                // Codeword counters are absent on the firmware this was built against, so these
                // stay 0 there; the alternate spellings cover builds that do publish them.
                Correctables = (long)GetChannelNumber(channel, "CorrectableCodewords", "Correcteds", "CorrErr"),
                Uncorrectables = (long)GetChannelNumber(channel, "UncorrectableCodewords", "Uncorrectables", "UncorrErr"),
            });
        }

        foreach (var channel in EnumerateChannels(html, UpstreamArrayRegex()))
        {
            var type = GetChannelString(channel, "ChannelType");
            if (string.IsNullOrWhiteSpace(type))
                type = GetChannelString(channel, "Modulation");

            stats.UpstreamChannels.Add(new UsChannel
            {
                ChannelId = (int)GetChannelNumber(channel, "ChannelID"),
                LockStatus = NormalizeLockStatus(GetChannelString(channel, "LockStatus")),
                ChannelType = type,
                Frequency = ParseFrequencyHz(GetChannelString(channel, "Frequency")),
                Power = ParsePowerDbmv(GetChannelString(channel, "PowerLevel")),
                SymbolRate = (long)GetChannelNumber(channel, "SymbolRate"),
            });
        }

        return stats;
    }

    private static IEnumerable<JsonElement> EnumerateChannels(string html, Regex arrayRegex)
    {
        var match = arrayRegex.Match(html);
        if (!match.Success)
            yield break;

        JsonDocument? document;
        try
        {
            document = JsonDocument.Parse(match.Groups[1].Value);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                yield break;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object)
                    yield return element.Clone();
            }
        }
    }

    /// <summary>
    /// The TG reports "ACTIVE" where the rest of the app expects DOCSIS "Locked"; without this
    /// mapping every locked-channel aggregate reads zero.
    /// </summary>
    internal static string NormalizeLockStatus(string raw)
    {
        var value = raw.Trim();
        if (value.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Locked", StringComparison.OrdinalIgnoreCase))
        {
            return "Locked";
        }

        return value;
    }

    /// <summary>
    /// Parse the TG frequency field to Hz. OFDM/OFDMA channels report a "start~end" band, which
    /// becomes its center. Values below 1 MHz are taken as MHz, which is how narrower firmware
    /// builds report the same field.
    /// </summary>
    internal static long ParseFrequencyHz(string raw)
    {
        var value = raw.Trim();
        if (value.Length == 0)
            return 0;

        var separator = value.IndexOf('~');
        if (separator >= 0)
        {
            var start = ParseLevel(value[..separator]);
            var end = ParseLevel(value[(separator + 1)..]);
            if (start == null || end == null)
                return 0;

            return ToHz((start.Value + end.Value) / 2);
        }

        var single = ParseLevel(value);
        return single == null ? 0 : ToHz(single.Value);
    }

    private static long ToHz(double value)
    {
        if (value <= 0)
            return 0;

        return value >= 1_000_000 ? (long)value : (long)(value * 1_000_000);
    }

    /// <summary>
    /// Parse the TG power field, which pairs both units as "-1.2 dBmV/1158.8 dBuV".
    /// </summary>
    internal static double? ParsePowerDbmv(string raw)
    {
        var value = raw.Trim();
        if (value.Length == 0)
            return null;

        var separator = value.IndexOf('/');
        if (separator >= 0)
            value = value[..separator];

        return ParseLevel(value);
    }

    /// <summary>
    /// Parse a leading number from a value that may carry a unit suffix ("41.8 dB").
    /// </summary>
    internal static double? ParseLevel(string raw)
    {
        var value = raw.Trim();
        if (value.Length == 0)
            return null;

        var end = 0;
        while (end < value.Length && (char.IsAsciiDigit(value[end]) || value[end] is '-' or '+' or '.'))
            end++;

        var numeric = value[..end];
        return double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string GetChannelString(JsonElement channel, string name)
    {
        if (!channel.TryGetProperty(name, out var value))
            return "";

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => "",
        };
    }

    private static double GetChannelNumber(JsonElement channel, params string[] names)
    {
        foreach (var name in names)
        {
            var raw = GetChannelString(channel, name);
            if (raw.Length == 0)
                continue;

            var parsed = ParseLevel(raw);
            if (parsed.HasValue)
                return parsed.Value;
        }

        return 0;
    }

    private static string AesCcmEncryptHex(byte[] key, byte[] nonce, byte[] plaintext, string associatedData)
    {
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLengthBytes];

        using var ccm = new AesCcm(key);
        ccm.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.ASCII.GetBytes(associatedData));

        return Convert.ToHexStringLower(ciphertext) + Convert.ToHexStringLower(tag);
    }

    private static string? AesCcmDecryptText(byte[] key, byte[] nonce, string encryptedHex, string associatedData)
    {
        if (!TryParseHex(encryptedHex, out var blob) || blob.Length <= TagLengthBytes)
            return null;

        var ciphertext = blob.AsSpan(0, blob.Length - TagLengthBytes);
        var tag = blob.AsSpan(blob.Length - TagLengthBytes);
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var ccm = new AesCcm(key);
            ccm.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.ASCII.GetBytes(associatedData));
        }
        catch (CryptographicException)
        {
            return null;
        }

        return Encoding.UTF8.GetString(plaintext);
    }

    private static bool IsSupportedNonceSize(int length)
    {
        var sizes = AesCcm.NonceByteSizes;
        if (length < sizes.MinSize || length > sizes.MaxSize)
            return false;

        return sizes.SkipSize == 0
            ? length == sizes.MinSize
            : (length - sizes.MinSize) % sizes.SkipSize == 0;
    }

    private static bool TryParseHex(string value, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromHexString(value.Trim());
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    internal static string? ExtractJsVar(string html, string name)
    {
        var escaped = Regex.Escape(name);
        string[] patterns =
        [
            $"""(?:var|let|const)\s+{escaped}\s*=\s*['"]([^'"]+)['"]""",
            $"""window\.{escaped}\s*=\s*['"]([^'"]+)['"]""",
            $"""{escaped}\s*=\s*['"]([^'"]+)['"]""",
        ];

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(html, pattern, RegexOptions.None, RegexTimeout);
            if (match.Success)
                return match.Groups[1].Value;
        }

        return null;
    }

    private static HttpClient CreateClient(CookieContainer cookies, string baseUrl, string? csrfNonce)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
            DefaultRequestHeaders =
            {
                { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36" },
                { "X-Requested-With", "XMLHttpRequest" },
                { "Origin", baseUrl },
                { "Referer", baseUrl + "/" },
            },
        };

        if (!string.IsNullOrEmpty(csrfNonce))
            client.DefaultRequestHeaders.TryAddWithoutValidation("csrfNonce", csrfNonce);

        return client;
    }

    private static string BuildBaseUrl(CmPollContext context)
    {
        var port = context.Port > 0 ? context.Port : 80;
        var scheme = port == 443 ? "https" : "http";
        var suffix = port is 80 or 443 ? "" : $":{port}";
        return $"{scheme}://{context.Host}{suffix}";
    }

    private static JsonDocument? TryParseJson(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetJsonString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public void Dispose()
    {
        _sessions.Clear();
    }

    [GeneratedRegex(@"json_dsData\s*=\s*(\[.+?\])\s*;", RegexOptions.Singleline)]
    private static partial Regex DownstreamArrayRegex();

    [GeneratedRegex(@"json_usData\s*=\s*(\[.+?\])\s*;", RegexOptions.Singleline)]
    private static partial Regex UpstreamArrayRegex();

    [GeneratedRegex("""createCookie\(\s*["']credential["']\s*,\s*["'](.+?)["']""")]
    private static partial Regex CredentialCookieRegex();

    private sealed record TgSession(string CsrfNonce, CookieContainer Cookies, string DeviceModel);
}
