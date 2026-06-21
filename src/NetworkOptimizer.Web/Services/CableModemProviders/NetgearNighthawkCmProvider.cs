using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;

namespace NetworkOptimizer.Web.Services.CableModemProviders;

/// <summary>
/// Cable modem provider for newer Netgear Nighthawk DOCSIS modems that run the
/// ".htm" web UI (CM1150V, CM2000, CM2050V).
///
/// These deliver channel data differently from the older Netgear modems handled by
/// <see cref="NetgearCmProvider"/>: <c>.htm</c> pages (e.g. <c>DocsisStatus.htm</c>)
/// whose channel data arrives as an inline JavaScript string
/// <c>var tagValueList = 'count|field|field|...'</c> split on <c>|</c>, NOT as the
/// server-rendered <c>dsTable</c>/<c>usTable</c> HTML tables the older modems emit.
///
/// The page carries four channel tables, each in its own Init function:
/// InitDsTableTagValue (SC-QAM downstream), InitDsOfdmTableTagValue (OFDM downstream),
/// InitUsTableTagValue (ATDMA upstream), and InitUsOfdmaTableTagValue (OFDMA upstream).
/// The login flow and all four field layouts are validated against a real CM2050V
/// DocsisStatus.htm capture (34 downstream = 32 QAM + 2 OFDM, 5 upstream = 4 ATDMA +
/// 1 OFDMA), cross-checked against the page's own column comments. See GitHub issue #820.
///
/// Auth varies within this family, so we try both modes (see
/// <see cref="LoginAndFetchStatusAsync"/>): the CM2000/CM2050V use a form login
/// (<c>POST /goform/Login</c> with <c>loginName</c>/<c>loginPassword</c>; the modem seeds
/// an <c>XSRF_TOKEN</c> cookie on first GET and ties the session to the source IP) while
/// the CM1150V uses HTTP Basic Auth. We attempt the form login first and fall back to Basic.
/// </summary>
public sealed class NetgearNighthawkCmProvider : ICableModemProvider
{
    /// <inheritdoc/>
    public string ProviderKey => "netgear-nighthawk";

    /// <inheritdoc/>
    public string DisplayName => "Netgear Nighthawk CM1150V/CM2000/CM2050V (HTTP)";

    private const string DefaultStatusPath = "/DocsisStatus.htm";
    private const string LoginPath = "/goform/Login";
    private const int TimeoutSeconds = 15;
    private const int MaxRetries = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    // The login form action carries a cache-buster id (observed as /goform/Login?id=730656415).
    // When we can fetch the login page first, we reuse the id baked into its form action;
    // otherwise we fall back to a generated one. Whether the modem actually validates this
    // id is one of the things to confirm against a full login HAR.
    private static readonly Regex LoginIdRx =
        new(@"goform/Login\?id=(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Pulls the inline `var tagValueList = '...'` string out of a named Init function on the
    // DocsisStatus page. The page has four channel tables, each with its own Init function:
    // InitDsTableTagValue (SC-QAM downstream), InitDsOfdmTableTagValue (OFDM downstream),
    // InitUsTableTagValue (ATDMA upstream), InitUsOfdmaTableTagValue (OFDMA upstream).
    // Each function also contains a commented-out decoy assignment using DOUBLE quotes, so
    // we anchor on the live SINGLE-quoted assignment only. The captured group is the raw
    // pipe-delimited list, leading channel count and all.
    private static Regex TagListRx(string initFunction) =>
        new(initFunction + @"\b.*?var\s+tagValueList\s*=\s*'([^']*)'",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ScQamDownstreamRx = TagListRx("InitDsTableTagValue");
    private static readonly Regex OfdmDownstreamRx = TagListRx("InitDsOfdmTableTagValue");
    private static readonly Regex AtdmaUpstreamRx = TagListRx("InitUsTableTagValue");
    private static readonly Regex OfdmaUpstreamRx = TagListRx("InitUsOfdmaTableTagValue");

    private readonly ILogger<NetgearNighthawkCmProvider> _logger;

    public NetgearNighthawkCmProvider(ILogger<NetgearNighthawkCmProvider> logger)
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
            _logger.LogWarning("Netgear Nighthawk CM poll requested but Host is empty (config {Id})", context.Id);
            return null;
        }

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var html = await LoginAndFetchStatusAsync(context, cancellationToken);
                if (html == null)
                {
                    _logger.LogWarning(
                        "Netgear Nighthawk CM at {Host} returned empty response (attempt {Attempt}/{Max})",
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
                    "Netgear Nighthawk CM {Name} polled: {DsCount} DS channels, {UsCount} US channels",
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
                    ex, "Transient error polling Netgear Nighthawk CM {Name} (attempt {Attempt}/{Max})",
                    context.Name, attempt, MaxRetries);
                await Task.Delay(RetryDelay, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error polling Netgear Nighthawk CM {Name} at {Host}", context.Name, context.Host);
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
            var html = await LoginAndFetchStatusAsync(context, cancellationToken);
            if (html == null)
                return (false, "No response from cable modem - check host and credentials");

            if (IsLoginPage(html))
                return (false, "Authentication failed - check username/password");

            var stats = ParseDocsisStatus(html, context);
            if (stats.DownstreamChannels.Count == 0 && stats.UpstreamChannels.Count == 0)
                return (false, "Connected but no DOCSIS channels found. Is the modem online, and is the status page path correct?");

            return (true, $"Connected - {stats.DownstreamChannels.Count} downstream, {stats.UpstreamChannels.Count} upstream channels detected");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetch the DocsisStatus page, handling the two auth styles seen across this modem
    /// family. The CM2000/CM2050V use a form login; the CM1150V uses HTTP Basic Auth. We
    /// try the form login first (confirmed for the CM2050V) and fall back to Basic auth,
    /// treating a response as authenticated only when it actually looks like the status
    /// page (carries the tagValueList init functions) rather than the login page.
    /// </summary>
    private async Task<string?> LoginAndFetchStatusAsync(CmPollContext context, CancellationToken cancellationToken)
    {
        var formHtml = await TryFormLoginFetchAsync(context, cancellationToken);
        if (IsDocsisStatusPage(formHtml))
            return formHtml;

        var basicHtml = await TryBasicAuthFetchAsync(context, cancellationToken);
        if (IsDocsisStatusPage(basicHtml))
            return basicHtml;

        // Neither auth landed on the status page; hand back whatever we got (likely the
        // login page) so the caller can surface an auth-failure message.
        return formHtml ?? basicHtml;
    }

    /// <summary>
    /// Form-login path (CM2000/CM2050V): GET the login page to seed any cookie and reuse
    /// the cache-buster id baked into its form action, POST credentials, then GET the
    /// status page. The modem ties the session to the source IP rather than a cookie, but
    /// we keep a CookieContainer in case a firmware variant sets one.
    /// </summary>
    private async Task<string?> TryFormLoginFetchAsync(CmPollContext context, CancellationToken cancellationToken)
    {
        var baseUrl = BuildBaseUrl(context);
        var statusUrl = $"{baseUrl}{StatusPath(context)}";

        using var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };

        string? loginId = null;
        try
        {
            var loginPage = await client.GetAsync($"{baseUrl}/", cancellationToken);
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
            _logger.LogDebug(ex, "Netgear Nighthawk CM: could not pre-fetch login page at {Host}", context.Host);
        }

        loginId ??= Random.Shared.Next(100_000_000, 999_999_999).ToString();

        var loginContent = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("loginName", context.Username ?? "admin"),
            new KeyValuePair<string, string>("loginPassword", context.Password ?? ""),
        });

        var loginResponse = await client.PostAsync($"{baseUrl}{LoginPath}?id={loginId}", loginContent, cancellationToken);
        if (!loginResponse.IsSuccessStatusCode)
        {
            _logger.LogDebug("Netgear Nighthawk CM form login returned {Status} for {Host}",
                loginResponse.StatusCode, context.Host);
            return null;
        }

        return await GetStringOrNullAsync(client, statusUrl, context, cancellationToken);
    }

    /// <summary>
    /// HTTP Basic Auth path (CM1150V): GET the status page directly with an Authorization
    /// header, no form post (matches the hdholm/ModemCheck behavior for that model).
    /// </summary>
    private async Task<string?> TryBasicAuthFetchAsync(CmPollContext context, CancellationToken cancellationToken)
    {
        var statusUrl = $"{BuildBaseUrl(context)}{StatusPath(context)}";

        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };

        var username = context.Username ?? "admin";
        var password = context.Password ?? "";
        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        return await GetStringOrNullAsync(client, statusUrl, context, cancellationToken);
    }

    private async Task<string?> GetStringOrNullAsync(
        HttpClient client, string url, CmPollContext context, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("Netgear Nighthawk CM status page returned {Status} for {Host}",
                response.StatusCode, context.Host);
            return null;
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(html) ? null : html;
    }

    private static string BuildBaseUrl(CmPollContext context)
    {
        var port = context.Port > 0 ? context.Port : 80;
        var portSuffix = port == 80 ? "" : $":{port}";
        return $"http://{context.Host}{portSuffix}";
    }

    private static string StatusPath(CmPollContext context) =>
        string.IsNullOrWhiteSpace(context.StatusPagePath) ? DefaultStatusPath : context.StatusPagePath;

    /// <summary>
    /// True when a fetched page is the DocsisStatus page (carries the tagValueList init
    /// functions) rather than the login page returned on an auth failure.
    /// </summary>
    private static bool IsDocsisStatusPage(string? html) =>
        html != null
        && (html.Contains("InitDsTableTagValue", StringComparison.OrdinalIgnoreCase)
            || html.Contains("InitUsTableTagValue", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Detect whether the response is the login page rather than the status page,
    /// which happens when authentication fails or the session was not established.
    /// </summary>
    private static bool IsLoginPage(string html)
    {
        return html.Contains("goform/Login", StringComparison.OrdinalIgnoreCase)
            && html.Contains("loginPassword", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parse the DocsisStatus.htm page. Channel data is carried in inline
    /// <c>var tagValueList = 'count|...'</c> strings inside InitDsTableTagValue /
    /// InitUsTableTagValue. Each list starts with the channel count, followed by a flat
    /// run of per-channel fields; fields-per-channel is derived from the count so the
    /// parser tolerates firmware that adds or drops a trailing column.
    /// </summary>
    private CableModemStats ParseDocsisStatus(string html, CmPollContext context)
    {
        var stats = new CableModemStats
        {
            Timestamp = DateTime.UtcNow,
            DeviceHost = context.Host,
            DeviceName = context.Name,
            DeviceModel = "Netgear Nighthawk",
        };

        ParseScQamDownstream(html, stats);
        ParseOfdmDownstream(html, stats);
        ParseAtdmaUpstream(html, stats);
        ParseOfdmaUpstream(html, stats);

        return stats;
    }

    // The four channel tables expose unused channels as fixed placeholder slots
    // ("Not Locked|Unknown|0|0|...") with channel id 0 and frequency 0. The modem's own UI
    // and other tooling count only real channels, so we skip placeholders by that signature.
    private static bool IsPlaceholder(int channelId, long frequency) => channelId == 0 && frequency == 0;

    /// <summary>
    /// SC-QAM downstream (InitDsTableTagValue). Field order per channel, confirmed against a
    /// real CM2050V DocsisStatus.htm (the page's own comment documents the same layout):
    /// [0] channel number, [1] lock status, [2] modulation, [3] channel id, [4] frequency,
    /// [5] power (dBmV), [6] SNR (dB), [7] correctables, [8] uncorrectables.
    /// </summary>
    private void ParseScQamDownstream(string html, CableModemStats stats)
    {
        var (fields, perChannel, count) = ExtractChannels(html, ScQamDownstreamRx, 9, "SC-QAM downstream");
        if (fields == null)
            return;

        for (int c = 0; c < count; c++)
        {
            var b = c * perChannel;
            var channelId = ParseInt(Field(fields, b + 3));
            var frequency = ParseFrequency(Field(fields, b + 4));
            if (IsPlaceholder(channelId, frequency))
                continue;

            stats.DownstreamChannels.Add(new DsChannel
            {
                ChannelId = channelId,
                LockStatus = Field(fields, b + 1),
                Modulation = Field(fields, b + 2),
                Frequency = frequency,
                Power = ParseDouble(Field(fields, b + 5)),
                Snr = ParseDouble(Field(fields, b + 6)),
                Correctables = ParseLong(Field(fields, b + 7)),
                Uncorrectables = ParseLong(Field(fields, b + 8)),
            });
        }
    }

    /// <summary>
    /// OFDM downstream (InitDsOfdmTableTagValue). Field order per channel, confirmed against a
    /// real CM2050V capture: [0] channel number, [1] lock status, [2] profile ids,
    /// [3] channel id, [4] frequency, [5] power (dBmV), [6] SNR/MER (dB),
    /// [7] active subcarrier range, [8] unerrored, [9] correctable, [10] uncorrectable codewords.
    /// OFDM codeword counts run to the billions and would swamp the SC-QAM correctable/
    /// uncorrectable totals, so (matching solentlabs/cable_modem_monitor) we deliberately do
    /// not fold them into the aggregates.
    /// </summary>
    private void ParseOfdmDownstream(string html, CableModemStats stats)
    {
        var (fields, perChannel, count) = ExtractChannels(html, OfdmDownstreamRx, 11, "OFDM downstream");
        if (fields == null)
            return;

        for (int c = 0; c < count; c++)
        {
            var b = c * perChannel;
            var channelId = ParseInt(Field(fields, b + 3));
            var frequency = ParseFrequency(Field(fields, b + 4));
            if (IsPlaceholder(channelId, frequency))
                continue;

            stats.DownstreamChannels.Add(new DsChannel
            {
                ChannelId = channelId,
                LockStatus = Field(fields, b + 1),
                Modulation = "OFDM",
                Frequency = frequency,
                Power = ParseDouble(Field(fields, b + 5)),
                Snr = ParseDouble(Field(fields, b + 6)),
            });
        }
    }

    /// <summary>
    /// ATDMA (SC-QAM) upstream (InitUsTableTagValue). Field order per channel, confirmed
    /// against a real CM2050V capture (the page's own comment documents the same layout):
    /// [0] channel number, [1] lock status, [2] channel type, [3] channel id,
    /// [4] symbol rate, [5] frequency, [6] power (dBmV).
    /// </summary>
    private void ParseAtdmaUpstream(string html, CableModemStats stats)
    {
        var (fields, perChannel, count) = ExtractChannels(html, AtdmaUpstreamRx, 7, "ATDMA upstream");
        if (fields == null)
            return;

        for (int c = 0; c < count; c++)
        {
            var b = c * perChannel;
            var channelId = ParseInt(Field(fields, b + 3));
            var frequency = ParseFrequency(Field(fields, b + 5));
            if (IsPlaceholder(channelId, frequency))
                continue;

            stats.UpstreamChannels.Add(new UsChannel
            {
                ChannelId = channelId,
                LockStatus = Field(fields, b + 1),
                ChannelType = Field(fields, b + 2),
                SymbolRate = ParseSymbolRate(Field(fields, b + 4)),
                Frequency = frequency,
                Power = ParseDouble(Field(fields, b + 6)),
            });
        }
    }

    /// <summary>
    /// OFDMA upstream (InitUsOfdmaTableTagValue). Field order per channel, confirmed against a
    /// real CM2050V capture: [0] channel number, [1] lock status, [2] profile ids,
    /// [3] channel id, [4] frequency, [5] power (dBmV). OFDMA has no fixed symbol rate.
    /// </summary>
    private void ParseOfdmaUpstream(string html, CableModemStats stats)
    {
        var (fields, perChannel, count) = ExtractChannels(html, OfdmaUpstreamRx, 6, "OFDMA upstream");
        if (fields == null)
            return;

        for (int c = 0; c < count; c++)
        {
            var b = c * perChannel;
            var channelId = ParseInt(Field(fields, b + 3));
            var frequency = ParseFrequency(Field(fields, b + 4));
            if (IsPlaceholder(channelId, frequency))
                continue;

            stats.UpstreamChannels.Add(new UsChannel
            {
                ChannelId = channelId,
                LockStatus = Field(fields, b + 1),
                ChannelType = "OFDMA",
                Frequency = frequency,
                Power = ParseDouble(Field(fields, b + 5)),
            });
        }
    }

    /// <summary>
    /// Locate a table's tagValueList, split it, and validate its width. Returns the flat field
    /// list, the derived fields-per-channel, and the channel count. fields is null when the
    /// table is absent or its width does not match (or exceed) the expected layout - we derive
    /// the width from the leading count so trailing columns added by other firmware are tolerated.
    /// </summary>
    private (List<string>? fields, int perChannel, int count) ExtractChannels(
        string html, Regex rx, int expectedWidth, string label)
    {
        var fields = ExtractTagValueFields(html, rx, out var count);
        if (fields == null || count <= 0)
            return (null, 0, 0);

        var perChannel = fields.Count / count;
        if (perChannel < expectedWidth)
        {
            _logger.LogDebug(
                "Netgear Nighthawk CM: {Label} field width {Width} below expected {Expected} for {Count} channels",
                label, perChannel, expectedWidth, count);
            return (null, 0, 0);
        }

        return (fields, perChannel, count);
    }

    /// <summary>
    /// Run a tagValueList regex against the page and split the captured string on '|'.
    /// The leading element is the channel count; the remainder are the flat per-channel
    /// fields (returned). Returns null when the list is absent or malformed.
    /// </summary>
    private static List<string>? ExtractTagValueFields(string html, Regex rx, out int count)
    {
        count = 0;
        var match = rx.Match(html);
        if (!match.Success)
            return null;

        // The page splits on "|" in JS; a trailing "|" yields an empty final token, so drop it.
        var tokens = match.Groups[1].Value.Split('|');
        if (tokens.Length < 2)
            return null;

        if (!int.TryParse(tokens[0].Trim(), out count) || count <= 0)
            return null;

        var fields = tokens.Skip(1).ToList();
        while (fields.Count > 0 && string.IsNullOrEmpty(fields[^1]))
            fields.RemoveAt(fields.Count - 1);

        return fields;
    }

    private static string Field(List<string> fields, int index) =>
        index >= 0 && index < fields.Count ? fields[index].Trim() : "";

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
        return double.TryParse(cleaned, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : null;
    }

    /// <summary>
    /// Parse a frequency that may arrive as raw Hz ("303000000"), "303000000 Hz",
    /// or "303 MHz", returning Hz.
    /// </summary>
    private static long ParseFrequency(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var trimmed = text.Trim();
        if (trimmed.EndsWith("MHz", StringComparison.OrdinalIgnoreCase))
        {
            var numPart = trimmed[..^3].Trim();
            return double.TryParse(numPart, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var mhz) ? (long)(mhz * 1_000_000) : 0;
        }

        var cleaned = StripUnits(trimmed);
        return long.TryParse(cleaned, out var hz) ? hz : 0;
    }

    private static long ParseSymbolRate(string text)
    {
        var cleaned = StripUnits(text);
        return long.TryParse(cleaned, out var val) ? val : 0;
    }

    /// <summary>
    /// Remove common DOCSIS unit suffixes (dBmV, dB, Hz, Ksym/sec, etc.) from a value.
    /// </summary>
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
}
