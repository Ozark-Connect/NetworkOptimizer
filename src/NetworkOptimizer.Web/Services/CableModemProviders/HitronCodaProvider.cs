using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetworkOptimizer.Core;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.CableModemProviders.Uci;
using NetworkOptimizer.Web.Services.Monitoring;

namespace NetworkOptimizer.Web.Services.CableModemProviders;

/// <summary>
/// Cable modem provider for Hitron CODA modems (CODA-5719, CODA-5519 and the firmware family that
/// shares their web UI), shipped by cable ISPs such as Mediacom. The web UI reads DOCSIS state from a
/// JSON API under /1/Device/CM behind a PHPSESSID session cookie.
/// </summary>
[VendorSpecific("Hitron", "CODA /1/Device/CM JSON API: PHPSESSID session, every value a string")]
public sealed partial class HitronCodaProvider : ICableModemProvider
{
    /// <inheritdoc/>
    public string ProviderKey => "hitron-coda";

    /// <inheritdoc/>
    public string DisplayName => "Hitron CODA (HTTP)";

    /// <inheritdoc/>
    public CmCredentialRequirement Credentials => CmCredentialRequirement.Required;

    /// <inheritdoc/>
    public bool BlankUsernameMeansAdmin => false;

    /// <inheritdoc/>
    public string? BlankUsername => DefaultUsername;

    /// <summary>The ISP account the CODA firmware ships with; a blank username signs in as it.</summary>
    internal const string DefaultUsername = "cusadmin";

    /// <summary>
    /// How long scheduled polls wait after a rejected sign-in before trying the same credentials again.
    /// Modem firmware commonly locks an account after repeated failures, which also locks the owner
    /// out of the web UI. Saving new credentials, or the Test button, tries again at once.
    /// </summary>
    internal static readonly TimeSpan RejectedSignInHold = TimeSpan.FromMinutes(10);

    internal const string DefaultBasePath = "/1/Device/CM";
    private const string LoginPagePath = "/webpages/login.html";
    private const string LoginPath = "/1/Device/Users/Login";
    private const string FallbackModel = "Hitron CODA";
    private const int TimeoutSeconds = 15;

    private readonly ILogger<HitronCodaProvider> _logger;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    // The gate keeps a probe and a poll of one modem from signing in twice: a second sign-in
    // replaces the first session's cookie on the modem.
    private sealed class Session
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public SessionIdentity? Identity { get; set; }
        public CookieContainer? Cookies { get; set; }
        public DateTime RejectedUntil { get; set; }
        public string? RejectedMessage { get; set; }
    }

    private sealed record SessionIdentity(Uri Origin, string Username, string PasswordHash);

    /// <summary>The modem answered with its sign-in redirect instead of data.</summary>
    private sealed class SessionExpiredException : Exception;

    /// <summary>The modem refused the username and password.</summary>
    private sealed class SignInRejectedException(string message) : InvalidOperationException(message);

    public HitronCodaProvider(ILogger<HitronCodaProvider> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task<PollResult<CableModemStats>> PollAsync(
        CmPollContext context,
        CancellationToken cancellationToken = default) =>
        PollAsync(context, honorRejectedHold: true, cancellationToken);

    private async Task<PollResult<CableModemStats>> PollAsync(
        CmPollContext context, bool honorRejectedHold, CancellationToken cancellationToken)
    {
        try
        {
            var stats = await FetchStatsAsync(context, honorRejectedHold, cancellationToken);

            _logger.LogDebug(
                "Hitron CODA {Name} polled: {Model}, {DsCount} DS channels, {UsCount} US channels, {Events} log entries",
                context.Name, stats.DeviceModel,
                stats.DownstreamChannels.Count, stats.UpstreamChannels.Count, stats.Events.Count);

            return PollResult<CableModemStats>.Ok(stats);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Hitron CODA {Name} poll failed ({FailureType}: {Message})",
                context.Name, ex.GetType().Name, ex.Message);
            return PollResult<CableModemStats>.Failed(DescribeFailure(ex, context.Host));
        }
    }

    /// <inheritdoc/>
    public async Task<(bool Success, string Message)> TestConnectionAsync(
        CmPollContext context,
        CancellationToken cancellationToken = default)
    {
        var result = await PollAsync(context, honorRejectedHold: false, cancellationToken);
        return result.Stats is { } stats
            ? (true, $"Connected to {stats.DeviceModel} - {stats.DownstreamChannels.Count} downstream, " +
                     $"{stats.UpstreamChannels.Count} upstream channels detected")
            : (false, result.FailureReason!);
    }

    private async Task<CableModemStats> FetchStatsAsync(CmPollContext context, bool honorRejectedHold, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
            throw new InvalidOperationException("No address is configured for this device.");
        if (string.IsNullOrEmpty(context.Password))
            throw new InvalidOperationException("Enter the Hitron modem's password in Settings.");

        var port = context.Port > 0 ? context.Port : 80;
        var baseUri = new UriBuilder(port == 443 ? "https" : "http", context.Host, port).Uri;
        var basePath = string.IsNullOrWhiteSpace(context.StatusPagePath)
            ? DefaultBasePath
            : "/" + context.StatusPagePath.Trim().Trim('/');
        var username = string.IsNullOrWhiteSpace(context.Username) ? DefaultUsername : context.Username.Trim();

        var session = _sessions.GetOrAdd(context.CacheKey, _ => new Session());
        var identity = new SessionIdentity(baseUri, username,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context.Password))));

        await session.Gate.WaitAsync(ct);
        try
        {
            if (session.Identity != identity)
            {
                session.Cookies = null;
                session.RejectedUntil = default;
                session.RejectedMessage = null;
                session.Identity = identity;
            }

            if (session.Cookies != null)
            {
                var cached = await TryReadAsync(session.Cookies, baseUri, basePath, context, ct);
                if (cached != null)
                    return cached;
            }

            // No session yet, or the modem dropped it (10 minute idle timeout, or a browser
            // sign-in replaced it): sign in once and read again.
            session.Cookies = null;
            if (honorRejectedHold && session.RejectedMessage != null && DateTime.UtcNow < session.RejectedUntil)
                throw new InvalidOperationException(session.RejectedMessage);

            var cookies = new CookieContainer();
            try
            {
                await LoginAsync(cookies, baseUri, username, context.Password, context.Dialer, ct);
                session.RejectedMessage = null;
            }
            catch (SignInRejectedException ex)
            {
                session.RejectedMessage = ex.Message;
                session.RejectedUntil = DateTime.UtcNow + RejectedSignInHold;
                throw;
            }
            var stats = await TryReadAsync(cookies, baseUri, basePath, context, ct)
                ?? throw new InvalidOperationException(
                    "The Hitron modem signed in but did not return DOCSIS data. Check the account's access level.");
            session.Cookies = cookies;
            return stats;
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async Task LoginAsync(
        CookieContainer cookies, Uri baseUri, string username, string password, IDeviceDialer dialer, CancellationToken ct)
    {
        using var client = CreateClient(dialer, cookies, baseUri);

        // The login page hands out the PHPSESSID the sign-in is bound to.
        try
        {
            using var page = await client.GetAsync(LoginPagePath, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogDebug("Hitron CODA: login page unavailable ({FailureType}); signing in without it", ex.GetType().Name);
        }

        var model = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = password,
        });
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["model"] = model });
        using var response = await client.PostAsync(LoginPath, content, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);

        using var document = ParseJson(body)
            ?? throw new InvalidDataException("The sign-in response was not JSON.");
        var root = document.RootElement;
        if (Text(root, "result").Equals("success", StringComparison.OrdinalIgnoreCase))
            return;

        var reason = Text(root, "errMsg").Trim();
        throw new SignInRejectedException(reason.Length > 0
            ? $"The Hitron modem rejected the sign-in ({reason}). Check the username and password."
            : "The Hitron modem rejected the sign-in. Check the username and password.");
    }

    /// <summary>
    /// Reads every page the DOCSIS WAN screen reads. Null when the modem answers with its sign-in
    /// redirect, so the caller can sign in again.
    /// </summary>
    private async Task<CableModemStats?> TryReadAsync(
        CookieContainer cookies, Uri baseUri, string basePath, CmPollContext context, CancellationToken ct)
    {
        using var client = CreateClient(context.Dialer, cookies, baseUri);

        var dsTask = GetRequiredAsync(client, $"{basePath}/DsInfo", ct);
        var usTask = GetRequiredAsync(client, $"{basePath}/UsInfo", ct);
        var dsOfdmTask = GetOptionalAsync(client, $"{basePath}/DsOfdm", context.Name, ct);
        var usOfdmTask = GetOptionalAsync(client, $"{basePath}/UsOfdm", context.Name, ct);
        var versionTask = GetOptionalAsync(client, $"{basePath}/Version", context.Name, ct);
        var logTask = GetOptionalAsync(client, $"{basePath}/Log", context.Name, ct);

        try
        {
            await Task.WhenAll(dsTask, usTask, dsOfdmTask, usOfdmTask, versionTask, logTask);
        }
        catch (SessionExpiredException)
        {
            DisposeAll(dsTask, usTask, dsOfdmTask, usOfdmTask, versionTask, logTask);
            return null;
        }
        catch
        {
            DisposeAll(dsTask, usTask, dsOfdmTask, usOfdmTask, versionTask, logTask);
            throw;
        }

        using var ds = dsTask.Result;
        using var us = usTask.Result;
        using var dsOfdm = dsOfdmTask.Result;
        using var usOfdm = usOfdmTask.Result;
        using var version = versionTask.Result;
        using var log = logTask.Result;

        return Parse(ds!.RootElement, us!.RootElement, dsOfdm?.RootElement, usOfdm?.RootElement,
            version?.RootElement, log?.RootElement, context);
    }

    private static void DisposeAll(params Task<JsonDocument?>[] tasks)
    {
        foreach (var task in tasks)
        {
            if (task.IsCompletedSuccessfully)
                task.Result?.Dispose();
        }
    }

    /// <summary>A channel table. Missing or malformed data fails the poll.</summary>
    private static async Task<JsonDocument?> GetRequiredAsync(HttpClient client, string path, CancellationToken ct)
    {
        using var response = await client.GetAsync(WithCacheBuster(path), ct);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new SessionExpiredException();
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        if (body.Contains("RedirectLoginIdentify", StringComparison.Ordinal))
            throw new SessionExpiredException();

        var document = ParseJson(body) ?? throw new InvalidDataException("Response did not contain DOCSIS channel data");
        var root = document.RootElement;
        if (Text(root, "errMsg").Equals("redirect", StringComparison.OrdinalIgnoreCase))
        {
            document.Dispose();
            throw new SessionExpiredException();
        }
        if (root.ValueKind != JsonValueKind.Object ||
            (root.TryGetProperty("errCode", out _) && Text(root, "errCode") != "000"))
        {
            document.Dispose();
            throw new InvalidDataException("Response did not contain DOCSIS channel data");
        }
        return document;
    }

    /// <summary>
    /// OFDM tables (absent on DOCSIS 3.0 models), the model, and the event log. A failure here
    /// leaves that part out rather than failing a poll whose channel tables arrived.
    /// </summary>
    private async Task<JsonDocument?> GetOptionalAsync(HttpClient client, string path, string name, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync(WithCacheBuster(path), ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var document = ParseJson(await response.Content.ReadAsStringAsync(ct));
            if (document == null)
                return null;

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                (root.TryGetProperty("errCode", out _) && Text(root, "errCode") != "000"))
            {
                document.Dispose();
                return null;
            }
            return document;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogDebug("Hitron CODA {Name}: {Path} unavailable ({FailureType})", name, path, ex.GetType().Name);
            return null;
        }
    }

    // The web UI appends a timestamp to every API call so nothing in between caches the reply.
    private static string WithCacheBuster(string path) =>
        $"{path}?_={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

    private static JsonDocument? ParseJson(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static CableModemStats Parse(
        JsonElement dsInfo,
        JsonElement usInfo,
        JsonElement? dsOfdm,
        JsonElement? usOfdm,
        JsonElement? version,
        JsonElement? log,
        CmPollContext context)
    {
        var stats = new CableModemStats
        {
            Timestamp = DateTime.UtcNow,
            DeviceHost = context.Host,
            DeviceName = context.Name,
            DeviceModel = version is { } v ? FormatDeviceModel(v) : FallbackModel,
            FirmwareVersion = version is { } fw && Text(fw, "SoftwareVersion").Trim() is { Length: > 0 } sw ? sw : null,
        };

        // SC-QAM downstream lists only channels the modem is tuned to, with no lock flag.
        foreach (var row in Rows(dsInfo, "Freq_List"))
        {
            var frequency = (long)(Number(row, "frequency") ?? 0);
            if (frequency <= 0)
                continue;

            stats.DownstreamChannels.Add(new DsChannel
            {
                ChannelId = (int)(Number(row, "channelId") ?? 0),
                LockStatus = "Locked",
                Modulation = FormatModulation(Text(row, "modulation")),
                Frequency = frequency,
                Power = Number(row, "signalStrength"),
                Snr = Number(row, "snr"),
                Correctables = Counter(row, "correcteds"),
                Uncorrectables = Counter(row, "uncorrect"),
            });
        }

        if (dsOfdm is { } ofdm)
        {
            foreach (var row in Rows(ofdm, "OFDMs_List"))
            {
                var band = ParseOccupiedBand(Text(row, "occupiedbw"));
                var locked = IsYes(row, "plclock") && IsYes(row, "ncplock") && IsYes(row, "mdc1lock");
                // An unused receiver slot reports no lock and no occupied band.
                if (!locked && band == null)
                    continue;

                stats.DownstreamChannels.Add(new DsChannel
                {
                    // The table numbers OFDM receivers from 0; it reports no DOCSIS channel ID.
                    ChannelId = (int)(Number(row, "receive") ?? 0),
                    LockStatus = locked ? "Locked" : "Not Locked",
                    Modulation = "OFDM",
                    // Midpoint of the occupied band, the spectrum position other providers report.
                    Frequency = band is { } b ? (long)Math.Round((b.StartMhz + b.EndMhz) / 2 * 1_000_000) : 0,
                    Power = Number(row, "plcpower"),
                    // No MER or codeword counters in this table.
                });
            }
        }

        foreach (var row in Rows(usInfo, "Freq_List"))
        {
            var frequency = (long)(Number(row, "frequency") ?? 0);
            var channelId = (int)(Number(row, "channelId") ?? 0);
            // The web UI renders channel ID 0 as "---": an unassigned slot.
            if (frequency <= 0 || channelId == 0)
                continue;

            stats.UpstreamChannels.Add(new UsChannel
            {
                ChannelId = channelId,
                LockStatus = "Locked",
                ChannelType = "SC-QAM",
                Modulation = FormatModulation(Text(row, "modulationType")),
                Frequency = frequency,
                Power = Number(row, "signalStrength"),
                // Reported in ksym/s (5120); stored in sym/s like every other provider.
                SymbolRate = (long)((Number(row, "symbolrate") ?? 0) * 1000),
            });
        }

        if (usOfdm is { } ofdma)
        {
            foreach (var row in Rows(ofdma, "OFDMAs_List"))
            {
                var state = Text(row, "state").Trim();
                if (state.Equals("DISABLED", StringComparison.OrdinalIgnoreCase) || (Number(row, "channelBw") ?? 0) <= 0)
                    continue;

                stats.UpstreamChannels.Add(new UsChannel
                {
                    ChannelId = (int)(Number(row, "uschindex") ?? 0),
                    LockStatus = state.Equals("OPERATE", StringComparison.OrdinalIgnoreCase) ? "Locked" : state,
                    ChannelType = "OFDMA",
                    // repPower is the total across the channel; the per-1.6 MHz figure is the one
                    // DOCSIS 3.1 reports and the one comparable to an SC-QAM channel's power.
                    Power = Number(row, "repPower1_6"),
                });
            }
        }

        if (log is { } events)
            stats.Events = ParseEventLog(events);

        return stats;
    }

    /// <summary>"Hitron CODA-5719" from /1/Device/CM/Version; the vendor name alone when no model is sent.</summary>
    internal static string FormatDeviceModel(JsonElement version)
    {
        var model = Text(version, "modelName").Trim();
        if (model.Length == 0)
            return FallbackModel;
        return model.StartsWith("Hitron", StringComparison.OrdinalIgnoreCase) ? model : $"Hitron {model}";
    }

    /// <summary>
    /// The DOCSIS event log, oldest first as the modem lists it. Priority is the DOCSIS level number,
    /// mapped to the names the web UI shows. The CM-MAC / CMTS-MAC trailer is dropped from the text.
    /// </summary>
    internal static List<CmEvent> ParseEventLog(JsonElement root)
    {
        var events = new List<(int Index, CmEvent Entry)>();
        foreach (var row in Rows(root, "Log_List"))
        {
            var text = Text(row, "event").Trim();
            if (text.Length == 0)
                continue;

            var time = Text(row, "time").Trim();
            var message = LogTrailerRx().Replace(text, "").Trim().TrimEnd(';').Trim();
            var entry = new CmEvent
            {
                Time = ParseEventTime(time),
                Level = PriorityName((int)(Number(row, "priority") ?? 0)),
                Text = message,
                // The index renumbers as the log rolls, so the identity is the time and the text.
                Raw = $"{time} {text}",
                Kind = UciStatsMapper.Classify(message),
            };
            events.Add(((int)(Number(row, "index") ?? events.Count), entry));
        }

        return events.OrderBy(e => e.Index).Select(e => e.Entry).ToList();
    }

    private static string PriorityName(int priority) => priority switch
    {
        1 => "Emergency",
        2 => "Alert",
        3 => "Critical",
        4 => "Error",
        5 => "Warning",
        6 => "Notice",
        7 => "Information",
        8 => "Debug",
        _ => "",
    };

    /// <summary>"2026-11-01T17:27:27-0500" to UTC. Null before the modem synced its clock.</summary>
    internal static DateTime? ParseEventTime(string text)
    {
        if (text.Length == 0)
            return null;
        // .NET wants a colon in the offset; the modem writes "-0500".
        var normalized = OffsetWithoutColonRx().Replace(text, "$1:$2");
        if (!DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var parsed))
            return null;
        var utc = parsed.UtcDateTime;
        return utc < UciStatsMapper.EarliestPlausible ? null : utc;
    }

    private static (double StartMhz, double EndMhz)? ParseOccupiedBand(string text)
    {
        var parts = text.Split('~', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var start) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var end) ||
            start <= 0 || end < start)
            return null;
        return (start, end);
    }

    /// <summary>"QAM256" stays; "64QAM" becomes "QAM64", the spelling the other providers carry.</summary>
    internal static string FormatModulation(string raw)
    {
        var value = raw.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
        return value.EndsWith("QAM", StringComparison.Ordinal) && value.Length > 3 ? "QAM" + value[..^3] : value;
    }

    private static IEnumerable<JsonElement> Rows(JsonElement root, string listName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(listName, out var list) ||
            list.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var row in list.EnumerateArray())
        {
            if (row.ValueKind == JsonValueKind.Object)
                yield return row;
        }
    }

    private static bool IsYes(JsonElement row, string name) =>
        Text(row, name).Trim().Equals("YES", StringComparison.OrdinalIgnoreCase);

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number => value.GetRawText(),
                _ => "",
            }
            : "";

    private static double? Number(JsonElement element, string name) =>
        double.TryParse(Text(element, name).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
        && double.IsFinite(number)
            ? number
            : null;

    private static long Counter(JsonElement element, string name) =>
        long.TryParse(Text(element, name).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : 0;

    private static string DescribeFailure(Exception ex, string host) => ex switch
    {
        InvalidOperationException => ex.Message,
        HttpRequestException or OperationCanceledException => HttpFailureSummary.Describe(ex, host),
        UriFormatException => "Check the Hitron modem's address and port in Settings.",
        _ => HttpFailureSummary.ForResponse(null, host),
    };

    private static HttpClient CreateClient(IDeviceDialer dialer, CookieContainer cookies, Uri baseUri)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = DeviceHttp.Via(dialer),
            UseProxy = false,
            UseCookies = true,
            CookieContainer = cookies,
            // An expired session redirects to the login page; it must read as expired, not as data.
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            // Like the other HTTPS modem providers, accept the device's self-signed certificate.
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        };

        var client = new HttpClient(handler)
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
        };
        // Sent as the web UI's own XHR calls send them.
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        client.DefaultRequestHeaders.Referrer = new Uri(baseUri, "/webpages/index.html");
        return client;
    }

    [GeneratedRegex(@";?\s*CM-MAC=.*$", RegexOptions.IgnoreCase)]
    private static partial Regex LogTrailerRx();

    [GeneratedRegex(@"([+-]\d{2})(\d{2})$")]
    private static partial Regex OffsetWithoutColonRx();
}
