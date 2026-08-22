using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.Monitoring;

namespace NetworkOptimizer.Web.Services.CableModemProviders;

/// <summary>
/// Cable modem provider for Technicolor CGA-series gateways (CGA437A, CGA4233VOO,
/// CGA4322DE, CGA6444VF and relatives) shipped by cable ISPs including VOO and
/// Vodafone. Two firmware families exist: one serves DOCSIS data at
/// /api/v1/sta_docsis_status, the other at /api/v1/modem/...; both share the same
/// double-PBKDF2 login but differ in CSRF token delivery and field naming.
/// </summary>
public sealed class TechnicolorCgaProvider : ICableModemProvider, IDisposable
{
    /// <inheritdoc/>
    public string ProviderKey => "technicolor-cga";

    /// <inheritdoc/>
    public string DisplayName => "Technicolor CGA Series (HTTP)";

    private const string DefaultDocsisPath = "/api/v1/sta_docsis_status";
    private const string ModemDocsisPath = "/api/v1/modem/exUSTbl,exDSTbl,USTbl,DSTbl,ErrTbl";
    private const string LoginPath = "/api/v1/session/login";
    private const string MenuPath = "/api/v1/session/menu";
    private const string DeviceInfoPath = "/api/v1/sta_device_info";
    private const string ModelNamePath = "/api/v1/system/ModelName";

    private const string SaltRequestPassword = "seeksalthash";
    private const int PbkdfIterations = 1000;
    private const int KeyLengthBytes = 16;
    private const int TimeoutSeconds = 15;

    private readonly ILogger<TechnicolorCgaProvider> _logger;
    private readonly ConcurrentDictionary<string, CgaSession> _sessions = new();

    public TechnicolorCgaProvider(ILogger<TechnicolorCgaProvider> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<PollResult<CableModemStats>> PollAsync(
        CmPollContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
        {
            _logger.LogWarning("Technicolor CGA poll requested but Host is empty (config {Id})", context.Id);
            return PollResult<CableModemStats>.Failed("No address is configured for this device.");
        }

        try
        {
            var stats = await TryPollAsync(context, cancellationToken);
            if (stats == null)
                return PollResult<CableModemStats>.Failed(
                    $"No stats could be read from {context.ConfiguredHost ?? context.Host}.");

            _logger.LogDebug(
                "Technicolor CGA {Name} polled: {Model}, {DsCount} DS channels, {UsCount} US channels",
                context.Name, stats.DeviceModel,
                stats.DownstreamChannels.Count, stats.UpstreamChannels.Count);

            return PollResult<CableModemStats>.Ok(stats);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _sessions.TryRemove(context.CacheKey, out _);
            _logger.LogWarning(ex, "Error polling Technicolor CGA {Name} at {Host}", context.Name, context.ConfiguredHost ?? context.Host);
            return PollResult<CableModemStats>.Failed(HttpFailureSummary.Describe(ex, context.ConfiguredHost ?? context.Host));
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
            return (false, "Password is required for the Technicolor web interface");

        try
        {
            var stats = await TryPollAsync(context, cancellationToken);
            if (stats != null)
            {
                return (true, $"Connected to {stats.DeviceModel} - " +
                    $"{stats.DownstreamChannels.Count} downstream, {stats.UpstreamChannels.Count} upstream channels detected");
            }

            return (false, "Could not read DOCSIS data from the Technicolor gateway. Check host, username, and password. " +
                "Some ISPs use a device-specific account name rather than admin.");
        }
        catch (Exception ex)
        {
            return (false, HttpFailureSummary.Describe(ex, context.ConfiguredHost ?? context.Host));
        }
    }

    private async Task<CableModemStats?> TryPollAsync(CmPollContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.Password))
            return null;

        var session = await EnsureSessionAsync(context, cancellationToken);
        if (session == null)
            return null;

        var payload = await TryFetchDocsisAsync(session, context, cancellationToken);
        if (payload == null)
        {
            // A token only stays valid for one signed-in session, so anything else signing in
            // invalidates ours. Re-authenticate once before giving up.
            _sessions.TryRemove(context.CacheKey, out _);
            session = await LoginAsync(context, cancellationToken);
            if (session == null)
                return null;

            payload = await TryFetchDocsisAsync(session, context, cancellationToken);
            if (payload == null)
                return null;
        }

        using (payload)
        {
            return ParseDocsis(payload.RootElement, context, session.DeviceModel);
        }
    }

    private async Task<CgaSession?> EnsureSessionAsync(CmPollContext context, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(context.CacheKey, out var cached))
            return cached;

        return await LoginAsync(context, cancellationToken);
    }

    private async Task<CgaSession?> LoginAsync(CmPollContext context, CancellationToken cancellationToken)
    {
        var baseUrl = BuildBaseUrl(context);
        var username = string.IsNullOrWhiteSpace(context.Username) ? "admin" : context.Username;
        var password = context.Password ?? "";

        var cookies = new CookieContainer();
        // The firmware expects this cookie before the first API call.
        cookies.Add(new Uri(baseUrl), new Cookie("cwd", "No", "/"));

        using var client = CreateClient(cookies, baseUrl, token: null);

        // Requesting the salts with logout=true also clears any session left behind by a
        // previous poll, which the firmware would otherwise refuse to replace.
        using var saltResponse = await PostFormAsync(
            client, baseUrl + LoginPath, username, SaltRequestPassword, cancellationToken);

        if (saltResponse == null)
            return null;

        var salt = GetJsonString(saltResponse.RootElement, "salt");
        var saltWebUi = GetJsonString(saltResponse.RootElement, "saltwebui");

        if (string.IsNullOrEmpty(salt) || string.IsNullOrEmpty(saltWebUi))
        {
            _logger.LogWarning(
                "Technicolor CGA {Name}: login did not return salt/saltwebui. The device may not use the CGA JSON API.",
                context.Name);
            return null;
        }

        // Both rounds salt with the ASCII text of the value, and the first round's output is
        // fed in as its lowercase hex string rather than as raw bytes.
        var firstHash = Convert.ToHexStringLower(Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(salt),
            PbkdfIterations, HashAlgorithmName.SHA256, KeyLengthBytes));

        var secondHash = Convert.ToHexStringLower(Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(firstHash), Encoding.UTF8.GetBytes(saltWebUi),
            PbkdfIterations, HashAlgorithmName.SHA256, KeyLengthBytes));

        using var loginResponse = await PostFormAsync(
            client, baseUrl + LoginPath, username, secondHash, cancellationToken);

        if (loginResponse == null)
            return null;

        var error = GetJsonString(loginResponse.RootElement, "error");
        if (!string.IsNullOrEmpty(error) && !error.Equals("ok", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Technicolor CGA {Name}: login rejected ({Error})", context.Name, error);
            return null;
        }

        // CGA437A firmware returns the CSRF token in the JSON body. CGA4233 (VOO) firmware
        // returns it as a Set-Cookie: auth=<value> instead.
        var token = GetJsonString(loginResponse.RootElement, "token") ?? "";
        if (string.IsNullOrEmpty(token))
        {
            var authCookie = cookies.GetCookies(new Uri(baseUrl))
                .FirstOrDefault(c => c.Name == "auth");
            if (authCookie != null)
                token = authCookie.Value;
        }

        var session = new CgaSession(token, cookies, "Technicolor CGA");

        using var authedClient = CreateClient(cookies, baseUrl, token);

        // Some firmware only arms the session once the menu has been requested.
        try
        {
            using var menuResponse = await authedClient.GetAsync(baseUrl + MenuPath, cancellationToken);
            _logger.LogDebug("Technicolor CGA {Name}: menu init returned {Status}", context.Name, menuResponse.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Technicolor CGA {Name}: menu init failed", context.Name);
        }

        session = session with { DeviceModel = await ReadDeviceModelAsync(authedClient, baseUrl, session.DeviceModel, cancellationToken) };
        _sessions[context.CacheKey] = session;

        _logger.LogDebug("Technicolor CGA {Name}: authenticated", context.Name);
        return session;
    }

    private async Task<JsonDocument?> PostFormAsync(
        HttpClient client,
        string url,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = password,
            ["logout"] = "true",
        });

        try
        {
            using var response = await client.PostAsync(url, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Technicolor CGA login POST returned {Status}", response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return TryParseJson(body);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Technicolor CGA login POST failed");
            return null;
        }
    }

    /// <summary>
    /// Fetch the DOCSIS payload, returning null when the session is no longer accepted so the
    /// caller can re-authenticate.
    /// </summary>
    private async Task<JsonDocument?> TryFetchDocsisAsync(
        CgaSession session,
        CmPollContext context,
        CancellationToken cancellationToken)
    {
        var baseUrl = BuildBaseUrl(context);
        var customPath = !string.IsNullOrWhiteSpace(context.StatusPagePath);
        var path = customPath ? context.StatusPagePath! : DefaultDocsisPath;

        using var client = CreateClient(session.Cookies, baseUrl, session.Token);

        var doc = await FetchJsonAsync(client, baseUrl, path, context.Name, cancellationToken);

        // CGA4233 firmware uses /api/v1/modem/... instead of sta_docsis_status.
        if (doc == null && !customPath)
            doc = await FetchJsonAsync(client, baseUrl, ModemDocsisPath, context.Name, cancellationToken);

        return doc;
    }

    private async Task<JsonDocument?> FetchJsonAsync(
        HttpClient client,
        string baseUrl,
        string path,
        string name,
        CancellationToken cancellationToken)
    {
        var separator = path.Contains('?') ? '&' : '?';
        var url = $"{baseUrl}{path}{separator}_={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}";

        try
        {
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "Technicolor CGA {Name}: DOCSIS request to {Path} returned {Status}",
                    name, path, response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var document = TryParseJson(body);
            if (document == null)
                return null;

            var error = GetJsonString(document.RootElement, "error");
            if (!string.IsNullOrEmpty(error) && !error.Equals("ok", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Technicolor CGA {Name}: DOCSIS request returned error {Error}", name, error);
                document.Dispose();
                return null;
            }

            return document;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Technicolor CGA {Name}: DOCSIS request to {Path} failed", name, path);
            return null;
        }
    }

    private static async Task<string> ReadDeviceModelAsync(
        HttpClient client,
        string baseUrl,
        string fallback,
        CancellationToken cancellationToken)
    {
        var model = await TryReadModelFromPath(client, baseUrl + DeviceInfoPath, cancellationToken)
                    ?? await TryReadModelFromPath(client, baseUrl + ModelNamePath, cancellationToken);

        if (string.IsNullOrWhiteSpace(model))
            return fallback;

        return model.StartsWith("Technicolor", StringComparison.OrdinalIgnoreCase) ? model : $"Technicolor {model}";
    }

    private static async Task<string?> TryReadModelFromPath(
        HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            var body = await client.GetStringAsync(url, cancellationToken);
            using var document = TryParseJson(body);
            if (document == null)
                return null;

            var root = document.RootElement.TryGetProperty("data", out var data) ? data : document.RootElement;
            return GetJsonString(root, "modelName") ?? GetJsonString(root, "model")
                ?? GetJsonString(root, "ModelName");
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    internal static CableModemStats ParseDocsis(JsonElement root, CmPollContext context, string deviceModel)
    {
        var stats = new CableModemStats
        {
            Timestamp = DateTime.UtcNow,
            DeviceHost = context.ConfiguredHost ?? context.Host,
            DeviceName = context.Name,
            DeviceModel = deviceModel,
        };

        // Channel arrays live under "data" on current firmware and at the root on older builds.
        var data = root.TryGetProperty("data", out var wrapped) ? wrapped : root;

        // CGA437A uses downstream/ofdm_downstream/upstream/ofdma_upstream.
        // CGA4233 (VOO) uses DSTbl/exDSTbl/USTbl/exUSTbl.
        foreach (var channel in EnumerateFirstArray(data, "downstream", "DSTbl"))
        {
            stats.DownstreamChannels.Add(new DsChannel
            {
                ChannelId = (int)GetNumber(channel, "channelid", "ChannelID"),
                LockStatus = NormalizeLockStatus(GetString(channel, "locked", "LockStatus")),
                Modulation = GetString(channel, "FFT", "Modulation"),
                Frequency = ParseFrequencyHz(GetString(channel, "CentralFrequency", "Frequency")),
                Power = ParseLevel(GetString(channel, "power", "PowerLevel")),
                Snr = ParseSnr(GetString(channel, "SNR", "SNRLevel")),
                Correctables = (long)GetNumber(channel, "CorrectableCodewords", "Correcteds", "corrError"),
                Uncorrectables = (long)GetNumber(channel, "UncorrectableCodewords", "Uncorrectables", "nonCorrError"),
            });
        }

        foreach (var channel in EnumerateFirstArray(data, "ofdm_downstream", "exDSTbl"))
        {
            var modulation = GetString(channel, "FFT_ofdm", "FFT");
            stats.DownstreamChannels.Add(new DsChannel
            {
                ChannelId = (int)GetNumber(channel, "channelid_ofdm", "channelid", "ChannelID"),
                LockStatus = NormalizeLockStatus(GetString(channel, "locked", "LockStatus")),
                Modulation = string.IsNullOrWhiteSpace(modulation) ? "OFDM" : modulation,
                Frequency = ParseFrequencyHz(GetString(channel, "CentralFrequency_ofdm", "CentralFrequency")),
                Power = ParseLevel(GetString(channel, "power_ofdm", "power", "PowerLevel")),
                Snr = ParseSnr(GetString(channel, "SNR_ofdm", "SNR", "SNRLevel")),
                Correctables = (long)GetNumber(channel, "CorrectableCodewords", "Correcteds", "corrError"),
                Uncorrectables = (long)GetNumber(channel, "UncorrectableCodewords", "Uncorrectables", "nonCorrError"),
            });
        }

        foreach (var channel in EnumerateFirstArray(data, "upstream", "USTbl"))
        {
            stats.UpstreamChannels.Add(BuildUpstream(channel, defaultType: null));
        }

        foreach (var channel in EnumerateFirstArray(data, "ofdma_upstream", "exUSTbl"))
        {
            stats.UpstreamChannels.Add(BuildUpstream(channel, defaultType: "OFDMA"));
        }

        return stats;
    }

    private static UsChannel BuildUpstream(JsonElement channel, string? defaultType)
    {
        var type = GetString(channel, "ChannelType");
        if (string.IsNullOrWhiteSpace(type))
            type = defaultType ?? GetString(channel, "FFT", "Modulation");

        return new UsChannel
        {
            ChannelId = (int)GetNumber(channel, "channelidup", "channelid", "ChannelID"),
            LockStatus = NormalizeLockStatus(GetString(channel, "locked", "LockStatus")),
            ChannelType = type,
            Frequency = ParseFrequencyHz(GetString(channel, "CentralFrequency", "Frequency")),
            Power = ParseLevel(GetString(channel, "power", "PowerLevel")),
            SymbolRate = (long)GetNumber(channel, "SymbolRate", "symbolrate"),
        };
    }

    private static IEnumerable<JsonElement> EnumerateFirstArray(JsonElement parent, params string[] names)
    {
        if (parent.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var name in names)
        {
            if (parent.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in array.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.Object)
                        yield return element;
                }
                yield break;
            }
        }
    }

    /// <summary>
    /// The API reports lock state with its own vocabulary; the rest of the app counts DOCSIS
    /// "Locked", so anything meaning locked has to arrive as that exact word.
    /// </summary>
    internal static string NormalizeLockStatus(string raw)
    {
        var value = raw.Trim();
        if (value.Length == 0)
            return "";

        return value.ToLowerInvariant() switch
        {
            "locked" or "active" or "yes" or "true" or "1" => "Locked",
            _ => value,
        };
    }

    /// <summary>
    /// Parse a frequency to Hz. Values below 1 MHz are taken as MHz, which is how some builds
    /// report the same field.
    /// </summary>
    internal static long ParseFrequencyHz(string raw)
    {
        var value = ParseLevel(raw);
        if (value is null or <= 0)
            return 0;

        return value.Value >= 1_000_000 ? (long)value.Value : (long)(value.Value * 1_000_000);
    }

    /// <summary>
    /// Parse SNR/MER, which some firmware reports as a negative MSE.
    /// </summary>
    internal static double? ParseSnr(string raw)
    {
        var value = ParseLevel(raw);
        return value.HasValue ? Math.Abs(value.Value) : null;
    }

    /// <summary>
    /// Parse a leading number from a value that may carry a unit suffix ("1.1 dBmV").
    /// </summary>
    internal static double? ParseLevel(string raw)
    {
        var value = raw.Trim();
        if (value.Length == 0)
            return null;

        var end = 0;
        while (end < value.Length && (char.IsAsciiDigit(value[end]) || value[end] is '-' or '+' or '.'))
            end++;

        return double.TryParse(value[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;

            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => "",
            };

            if (text.Length > 0)
                return text;
        }

        return "";
    }

    private static double GetNumber(JsonElement element, params string[] names)
        => ParseLevel(GetString(element, names)) ?? 0;

    private static HttpClient CreateClient(CookieContainer cookies, string baseUrl, string? token)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };

        // The firmware checks User-Agent, X-Requested-With, and Referer on every request,
        // including the session initialization that follows login.
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
            DefaultRequestHeaders =
            {
                { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36" },
                { "X-Requested-With", "XMLHttpRequest" },
                { "Referer", baseUrl + "/" },
            },
        };

        if (!string.IsNullOrEmpty(token))
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-CSRF-TOKEN", token);

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
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public void Dispose()
    {
        _sessions.Clear();
    }

    private sealed record CgaSession(string Token, CookieContainer Cookies, string DeviceModel);
}
