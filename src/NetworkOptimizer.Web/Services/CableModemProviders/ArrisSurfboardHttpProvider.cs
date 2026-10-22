using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.Monitoring;

namespace NetworkOptimizer.Web.Services.CableModemProviders;

/// <summary>
/// Cable modem provider for ARRIS Surfboard modems (SB8200, SB6183, SB6141, T25).
/// Supports SB8200 token-based auth (HTTPS), the SB6183 simple page fetch (HTTP), and the SB6141's
/// transposed signal page (HTTP, channels as columns).
/// Auto-detects model by trying SB8200 HTTPS first, then SB6183 HTTP, then SB6141 HTTP. The SB6141
/// step is gated on page content, and a detected SB6141 is remembered per config.
/// </summary>
public sealed class ArrisSurfboardHttpProvider : ICableModemProvider, IDisposable
{
    /// <inheritdoc/>
    public string ProviderKey => "arris-surfboard";

    /// <inheritdoc/>
    public string DisplayName => "ARRIS Surfboard (HTTP)";

    /// <inheritdoc/>
    public CmCredentialRequirement Credentials => CmCredentialRequirement.Optional;

    /// <inheritdoc/>
    public bool BlankUsernameMeansAdmin => false;

    private const string Sb8200StatusPath = "/cmconnectionstatus.html";
    private const string Sb6183StatusPath = "/RgConnect.asp";
    private const string Sb6141SignalPath = "/cmSignalData.htm";
    private const string Sb6141IndexPath = "/indexData.htm";
    private const string Sb6141HelpPath = "/cmHelpData.htm";
    private const int TimeoutSeconds = 15;

    private static readonly Regex Sb6141ModelRx = new(@"Model Name:\s*([^<\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Sb6141FirmwareRx = new(@"Firmware Name:\s*([^<\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Sb6141UptimeRx = new(
        @"^(?:(\d+)\s*days?\s*)?(\d+)\s*h\s*:\s*(\d+)\s*m\s*:\s*(\d+)\s*s$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UpstreamModulationRx = new(@"(\d+)\s*-?\s*QAM|QPSK|BPSK", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ILogger<ArrisSurfboardHttpProvider> _logger;

    /// <summary>
    /// Cached auth tokens keyed by CmConfiguration.Id.
    /// SB8200 requires token-based auth; tokens are cached until they expire.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _tokenCache = new();

    /// <summary>
    /// Configs whose modem served the SB6141 signal page, keyed by <see cref="CmPollContext.CacheKey"/>.
    /// Their polls go straight to the SB6141 pages and skip the SB8200 and SB6183 probes.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _sb6141Configs = new();

    public ArrisSurfboardHttpProvider(ILogger<ArrisSurfboardHttpProvider> logger)
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
            _logger.LogWarning("ARRIS Surfboard poll requested but Host is empty (config {Id})", context.Id);
            return PollResult<CableModemStats>.Failed("No address is configured for this device.");
        }

        try
        {
            if (_sb6141Configs.ContainsKey(context.CacheKey))
            {
                var cached = await TrySb6141Async(context, cancellationToken);
                if (cached != null)
                    return PollResult<CableModemStats>.Ok(cached);

                _sb6141Configs.TryRemove(context.CacheKey, out _);
                _logger.LogDebug("ARRIS SB6141 {Name}: cached page format failed, re-detecting", context.Name);
            }

            // Try SB8200 (HTTPS with token auth) first
            var html = await TrySb8200Async(context, cancellationToken);
            if (html != null)
            {
                var stats = ParseSb8200(html, context);
                _logger.LogDebug(
                    "ARRIS SB8200 {Name} polled: {DsCount} DS channels, {UsCount} US channels",
                    context.Name, stats.DownstreamChannels.Count, stats.UpstreamChannels.Count);

                // Logout to free the session
                await LogoutAsync(context, cancellationToken);
                return PollResult<CableModemStats>.Ok(stats);
            }

            // Fall back to SB6183 (HTTP, no auth)
            CableModemStats? sb6183Stats = null;
            html = await TrySb6183Async(context, cancellationToken);
            if (html != null)
            {
                // A Status Page Path set to the SB6141 signal page lands here; parse it by content.
                if (IsSb6141SignalPage(html))
                    return PollResult<CableModemStats>.Ok(await CompleteSb6141Async(html, context, cancellationToken));

                sb6183Stats = ParseSb6183(html, context);
                if (HasChannels(sb6183Stats))
                    return Sb6183Ok(sb6183Stats, context);
            }

            // An SB6183 page with no channels may be another model's 200 on an unknown path, so the
            // SB6141 gets a turn before that empty result is reported as before.
            var sb6141Stats = await TrySb6141Async(context, cancellationToken);
            if (sb6141Stats != null)
                return PollResult<CableModemStats>.Ok(sb6141Stats);

            if (sb6183Stats != null)
                return Sb6183Ok(sb6183Stats, context);

            _logger.LogWarning("ARRIS Surfboard {Name} at {Host}: SB8200, SB6183, and SB6141 fetch failed",
                context.Name, context.Host);
            return PollResult<CableModemStats>.Failed($"No stats could be read from {context.Host}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error polling ARRIS Surfboard {Name} at {Host}", context.Name, context.Host);
            return PollResult<CableModemStats>.Failed(HttpFailureSummary.Describe(ex, context.Host));
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
            // Try SB8200 first
            var html = await TrySb8200Async(context, cancellationToken);
            if (html != null)
            {
                await LogoutAsync(context, cancellationToken);
                return (true, "Connected to ARRIS SB8200 (HTTPS with token auth)");
            }

            // Try SB6183
            html = await TrySb6183Async(context, cancellationToken);
            if (html != null)
            {
                if (IsSb6141SignalPage(html))
                    return (true, "Connected to ARRIS SB6141 (HTTP)");
                if (HasChannels(ParseSb6183(html, context)))
                    return (true, "Connected to ARRIS SB6183 (HTTP)");
            }

            // Try SB6141, then report an SB6183 page without channels as before
            if (await TrySb6141Async(context, cancellationToken) != null)
                return (true, "Connected to ARRIS SB6141 (HTTP)");
            if (html != null)
                return (true, "Connected to ARRIS SB6183 (HTTP)");

            return (false, "Could not connect via HTTPS (SB8200) or HTTP (SB6183, SB6141). Check host and credentials.");
        }
        catch (Exception ex)
        {
            return (false, HttpFailureSummary.Describe(ex, context.Host));
        }
    }

    /// <summary>
    /// Attempt SB8200 fetch via HTTPS with token-based authentication.
    /// Returns HTML on success, null if this model/auth method doesn't work.
    /// </summary>
    private async Task<string?> TrySb8200Async(CmPollContext context, CancellationToken cancellationToken)
    {
        var statusPath = string.IsNullOrWhiteSpace(context.StatusPagePath)
            ? Sb8200StatusPath
            : context.StatusPagePath;

        var port = context.Port > 0 ? context.Port : 443;
        var portSuffix = port == 443 ? "" : $":{port}";
        var baseUrl = $"https://{context.Host}{portSuffix}";
        var statusUrl = $"{baseUrl}{statusPath}";

        using var client = CreateHttpClient(context.Dialer, ignoreSslErrors: true);

        // Try with cached token first
        if (_tokenCache.TryGetValue(context.CacheKey, out var cachedToken))
        {
            var html = await FetchWithTokenAsync(client, statusUrl, cachedToken, cancellationToken);
            if (html != null && !IsAuthPage(html))
                return html;

            // Cached token expired
            _tokenCache.TryRemove(context.CacheKey, out _);
            _logger.LogDebug("ARRIS SB8200 cached token expired for {Name}, re-authenticating", context.Name);
        }

        // Authenticate to get a new token
        var token = await AuthenticateAsync(client, statusUrl, context, cancellationToken);
        if (token == null)
            return null;

        _tokenCache[context.CacheKey] = token;

        // Fetch with new token
        var result = await FetchWithTokenAsync(client, statusUrl, token, cancellationToken);
        if (result != null && !IsAuthPage(result))
            return result;

        // Auth failed completely
        _tokenCache.TryRemove(context.CacheKey, out _);
        return null;
    }

    /// <summary>
    /// Attempt SB6183 fetch via plain HTTP (no auth required).
    /// </summary>
    private async Task<string?> TrySb6183Async(CmPollContext context, CancellationToken cancellationToken)
    {
        var statusPath = string.IsNullOrWhiteSpace(context.StatusPagePath)
            ? Sb6183StatusPath
            : context.StatusPagePath;

        var port = context.Port > 0 ? context.Port : 80;
        var portSuffix = port == 80 ? "" : $":{port}";
        var url = $"http://{context.Host}{portSuffix}{statusPath}";

        try
        {
            using var client = CreateHttpClient(context.Dialer, ignoreSslErrors: false);
            var response = await client.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return null;

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(html) ? null : html;
        }
        catch (HttpRequestException)
        {
            // Expected when host doesn't support plain HTTP
            return null;
        }
    }

    private PollResult<CableModemStats> Sb6183Ok(CableModemStats stats, CmPollContext context)
    {
        _logger.LogDebug(
            "ARRIS SB6183 {Name} polled: {DsCount} DS channels, {UsCount} US channels",
            context.Name, stats.DownstreamChannels.Count, stats.UpstreamChannels.Count);
        return PollResult<CableModemStats>.Ok(stats);
    }

    private static bool HasChannels(CableModemStats stats) =>
        stats.DownstreamChannels.Count > 0 || stats.UpstreamChannels.Count > 0;

    /// <summary>
    /// Attempt the SB6141 (HTTP, no auth): fetch <c>/cmSignalData.htm</c> and accept it only when it is
    /// the SB6141 signal page. Returns the parsed stats, or null when this is not an SB6141.
    /// </summary>
    private async Task<CableModemStats?> TrySb6141Async(CmPollContext context, CancellationToken cancellationToken)
    {
        string? html;
        try
        {
            html = await FetchSb6141PageAsync(context, Sb6141SignalPath, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "ARRIS SB6141 signal page fetch failed for {Host}", context.Host);
            return null;
        }

        if (html == null || !IsSb6141SignalPage(html))
            return null;

        return await CompleteSb6141Async(html, context, cancellationToken);
    }

    /// <summary>
    /// Read the SB6141's status and help pages to go with an already fetched signal page, parse all
    /// three, and remember the format for this config. The two extra pages are optional.
    /// </summary>
    private async Task<CableModemStats> CompleteSb6141Async(
        string signalHtml, CmPollContext context, CancellationToken cancellationToken)
    {
        var indexHtml = await TryFetchOptionalSb6141PageAsync(context, Sb6141IndexPath, cancellationToken);
        var helpHtml = await TryFetchOptionalSb6141PageAsync(context, Sb6141HelpPath, cancellationToken);

        var stats = ParseSb6141(signalHtml, indexHtml, helpHtml, context);
        if (context.Id > 0)
            _sb6141Configs[context.CacheKey] = true;

        _logger.LogDebug(
            "ARRIS SB6141 {Name} polled: {DsCount} DS channels, {UsCount} US channels",
            context.Name, stats.DownstreamChannels.Count, stats.UpstreamChannels.Count);
        return stats;
    }

    private async Task<string?> TryFetchOptionalSb6141PageAsync(
        CmPollContext context, string path, CancellationToken cancellationToken)
    {
        try
        {
            return await FetchSb6141PageAsync(context, path, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogDebug(ex, "ARRIS SB6141 {Path} fetch failed for {Host}; continuing without it", path, context.Host);
            return null;
        }
    }

    /// <summary>
    /// GET one SB6141 page over plain HTTP. Its NET-DK/1.0 server is the one that corrupts header
    /// lines on the Netgear CM700, so a response HttpClient rejects as malformed is re-read through
    /// <see cref="LenientHttp"/>. Returns null on a non-success status or an empty body.
    /// </summary>
    private async Task<string?> FetchSb6141PageAsync(CmPollContext context, string path, CancellationToken cancellationToken)
    {
        var port = context.Port > 0 ? context.Port : 80;
        var portSuffix = port == 80 ? "" : $":{port}";
        var url = $"http://{context.Host}{portSuffix}{path}";

        try
        {
            using var client = CreateHttpClient(context.Dialer, ignoreSslErrors: false);
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(html) ? null : html;
        }
        catch (HttpRequestException ex) when (LenientHttp.IsMalformedHttpResponse(ex))
        {
            _logger.LogDebug(ex, "ARRIS SB6141 {Url}: malformed HTTP response; retrying via lenient raw-socket reader", url);
            return await FetchSb6141PageRawAsync(context.Dialer, context.Host, port, path, cancellationToken);
        }
    }

    private static async Task<string?> FetchSb6141PageRawAsync(
        IDeviceDialer dialer, string host, int port, string path, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

        try
        {
            var raw = await LenientHttp.RawHttpGetAsync(dialer, host, port, path, authHeader: null, cookie: null, timeoutCts.Token);
            var response = LenientHttp.ParseRawHttpResponse(raw, raw.Length);
            if (response.StatusCode is < 200 or >= 300)
                return null;
            return string.IsNullOrWhiteSpace(response.Body) ? null : response.Body;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException($"ARRIS SB6141 raw fetch of {path} timed out after {TimeoutSeconds}s");
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException)
        {
            throw new HttpRequestException($"ARRIS SB6141 raw fetch of {path} failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// True when a page is the SB6141 signal page: transposed tables headed "Bonding Channel Value"
    /// plus the separate "Signal Stats (Codewords)" table. Both are required so a page from another
    /// model is never diverted to this parser.
    /// </summary>
    internal static bool IsSb6141SignalPage(string html) =>
        html.Contains("Bonding Channel Value", StringComparison.OrdinalIgnoreCase)
        && html.Contains("Signal Stats (Codewords)", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// SB8200 token auth flow:
    /// 1. Base64 encode credentials
    /// 2. GET statusUrl?login_{base64Creds} with Basic auth header
    /// 3. Response body contains the session token
    /// </summary>
    private async Task<string?> AuthenticateAsync(
        HttpClient client,
        string statusUrl,
        CmPollContext context,
        CancellationToken cancellationToken)
    {
        var username = context.Username ?? "admin";
        var password = context.Password ?? "";
        var base64Creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));

        var loginUrl = $"{statusUrl}?login_{base64Creds}";

        using var request = new HttpRequestMessage(HttpMethod.Get, loginUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", base64Creds);

        try
        {
            var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("ARRIS SB8200 auth returned {Status} for {Host}",
                    response.StatusCode, context.Host);
                return null;
            }

            var token = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogDebug("ARRIS SB8200 auth returned empty token for {Host}", context.Host);
                return null;
            }

            return token.Trim();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "ARRIS SB8200 auth request failed for {Host}", context.Host);
            return null;
        }
    }

    /// <summary>
    /// Fetch the status page using an authenticated session token.
    /// GET statusUrl?ct_{token}
    /// </summary>
    private async Task<string?> FetchWithTokenAsync(
        HttpClient client,
        string statusUrl,
        string token,
        CancellationToken cancellationToken)
    {
        var url = $"{statusUrl}?ct_{token}";

        try
        {
            var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// Logout after polling to free the session slot on the modem.
    /// </summary>
    private async Task LogoutAsync(CmPollContext context, CancellationToken cancellationToken)
    {
        var port = context.Port > 0 ? context.Port : 443;
        var portSuffix = port == 443 ? "" : $":{port}";
        var logoutUrl = $"https://{context.Host}{portSuffix}/logout.html";

        try
        {
            using var client = CreateHttpClient(context.Dialer, ignoreSslErrors: true);
            await client.GetAsync(logoutUrl, cancellationToken);
        }
        catch
        {
            // Logout is best-effort
        }
    }

    /// <summary>
    /// Detect if the response is an auth/login page rather than the status data.
    /// SB8200 shows "Password:" text when auth is required.
    /// </summary>
    private static bool IsAuthPage(string html)
    {
        return html.Contains("Password:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parse SB8200 HTML. DS table is the second table, US table is the third.
    /// The HTML often contains malformed extra &lt;/tr&gt; tags after "Bonded Channels" text.
    /// </summary>
    private CableModemStats ParseSb8200(string html, CmPollContext context)
    {
        // Fix malformed HTML: strip extra </tr> after "Bonded Channels"
        html = FixMalformedHtml(html);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var stats = new CableModemStats
        {
            Timestamp = DateTime.UtcNow,
            DeviceHost = context.Host,
            DeviceName = context.Name,
            DeviceModel = "ARRIS SB8200",
        };

        var tables = doc.DocumentNode.SelectNodes("//table");
        if (tables == null || tables.Count < 3)
        {
            _logger.LogDebug("ARRIS SB8200 {Name}: expected 3+ tables, found {Count}",
                context.Name, tables?.Count ?? 0);
            return stats;
        }

        // SB8200: tables[1] = downstream, tables[2] = upstream
        ParseDownstreamTable(tables[1], stats);
        ParseUpstreamTable(tables[2], stats);

        return stats;
    }

    /// <summary>
    /// Parse SB6183 HTML. DS table is the third table, US table is the fourth.
    /// </summary>
    private CableModemStats ParseSb6183(string html, CmPollContext context)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var stats = new CableModemStats
        {
            Timestamp = DateTime.UtcNow,
            DeviceHost = context.Host,
            DeviceName = context.Name,
            DeviceModel = "ARRIS SB6183",
        };

        var tables = doc.DocumentNode.SelectNodes("//table");
        if (tables == null || tables.Count < 4)
        {
            _logger.LogDebug("ARRIS SB6183 {Name}: expected 4+ tables, found {Count}",
                context.Name, tables?.Count ?? 0);
            return stats;
        }

        // SB6183: tables[2] = downstream, tables[3] = upstream
        ParseDownstreamTable(tables[2], stats);
        ParseUpstreamTable(tables[3], stats);

        return stats;
    }

    /// <summary>
    /// Parse the SB6141 pages. <c>/cmSignalData.htm</c> holds three transposed tables (Downstream,
    /// Upstream, Signal Stats (Codewords)) with one channel per column. <c>/indexData.htm</c> gives the
    /// registration status and uptime, <c>/cmHelpData.htm</c> the model and firmware; either may be null.
    /// </summary>
    internal static CableModemStats ParseSb6141(string signalHtml, string? indexHtml, string? helpHtml, CmPollContext context)
    {
        var stats = new CableModemStats
        {
            Timestamp = DateTime.UtcNow,
            DeviceHost = context.Host,
            DeviceName = context.Name,
            DeviceModel = "ARRIS SB6141",
        };

        var doc = new HtmlDocument();
        doc.LoadHtml(signalHtml);
        var tables = ReadTransposedTables(doc);

        // The page lists bonded channels only and has no lock column, so a listed channel is locked.
        if (FindTable(tables, "Downstream") is { } ds)
        {
            var codewords = FindTable(tables, "Signal Stats") is { } cw ? CodewordsByChannel(cw) : new();
            var ids = Row(ds, "Channel ID");
            for (int i = 0; i < ids.Count; i++)
            {
                var frequency = ParseFrequency(Cell(ds, "Frequency", i));
                if (frequency == 0)
                    continue;

                var channelId = ParseInt(ids[i]);
                codewords.TryGetValue(channelId, out var counts);
                stats.DownstreamChannels.Add(new DsChannel
                {
                    ChannelId = channelId,
                    LockStatus = "Locked",
                    Modulation = Cell(ds, "Downstream Modulation", i),
                    Frequency = frequency,
                    Power = ParseDouble(Cell(ds, "Power Level", i)),
                    Snr = ParseDouble(Cell(ds, "Signal to Noise Ratio", i)),
                    Correctables = counts.Correctables,
                    Uncorrectables = counts.Uncorrectables,
                });
            }
        }

        if (FindTable(tables, "Upstream") is { } us)
        {
            var ids = Row(us, "Channel ID");
            for (int i = 0; i < ids.Count; i++)
            {
                var frequency = ParseFrequency(Cell(us, "Frequency", i));
                if (frequency == 0)
                    continue;

                var ranging = Cell(us, "Ranging Status", i);
                stats.UpstreamChannels.Add(new UsChannel
                {
                    ChannelId = ParseInt(ids[i]),
                    LockStatus = ranging.Equals("Success", StringComparison.OrdinalIgnoreCase) ? "Locked" : ranging,
                    Frequency = frequency,
                    Power = ParseDouble(Cell(us, "Power Level", i)),
                    SymbolRate = ParseSymbolRatePerSecond(Cell(us, "Symbol Rate", i)),
                    Modulation = HighestOrderModulation(Cell(us, "Upstream Modulation", i)),
                });
            }
        }

        if (!string.IsNullOrWhiteSpace(indexHtml))
            ApplySb6141Status(indexHtml, stats);

        if (!string.IsNullOrWhiteSpace(helpHtml))
        {
            if (Sb6141ModelRx.Match(helpHtml) is { Success: true } model)
                stats.DeviceModel = "ARRIS " + model.Groups[1].Value.Trim();
            if (Sb6141FirmwareRx.Match(helpHtml) is { Success: true } firmware)
                stats.FirmwareVersion = firmware.Groups[1].Value.Trim();
        }

        return stats;
    }

    /// <summary>
    /// Read each table that has a header row into its title (first header cell) and a map of row
    /// label to value cells. Only a table's own rows are read, so the note table nested inside the
    /// downstream Power Level label adds no row, and the label is the cell's own text without it.
    /// </summary>
    private static List<(string Title, Dictionary<string, List<string>> Rows)> ReadTransposedTables(HtmlDocument doc)
    {
        var result = new List<(string, Dictionary<string, List<string>>)>();
        foreach (var table in doc.DocumentNode.SelectNodes("//table") ?? Enumerable.Empty<HtmlNode>())
        {
            var rows = table.SelectNodes("./tbody/tr | ./tr");
            var title = rows?.FirstOrDefault()?.SelectSingleNode("./th") is { } th ? CleanCell(th) : null;
            if (rows == null || string.IsNullOrEmpty(title))
                continue;

            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows.Skip(1))
            {
                var cells = row.SelectNodes("./td");
                if (cells == null || cells.Count < 2)
                    continue;

                var label = CleanText(string.Concat(cells[0].ChildNodes
                    .Where(n => n.NodeType == HtmlNodeType.Text)
                    .Select(n => n.InnerText)));
                if (label.Length > 0)
                    map.TryAdd(label, cells.Skip(1).Select(CleanCell).ToList());
            }

            result.Add((title, map));
        }

        return result;
    }

    private static Dictionary<string, List<string>>? FindTable(
        List<(string Title, Dictionary<string, List<string>> Rows)> tables, string titlePrefix) =>
        tables.FirstOrDefault(t => t.Title.StartsWith(titlePrefix, StringComparison.OrdinalIgnoreCase)).Rows;

    private static List<string> Row(Dictionary<string, List<string>> table, string label) =>
        table.TryGetValue(label, out var cells) ? cells : new List<string>();

    private static string Cell(Dictionary<string, List<string>> table, string label, int index)
    {
        var cells = Row(table, label);
        return index < cells.Count ? cells[index] : "";
    }

    /// <summary>
    /// Map channel ID to its codeword counters. Joined by ID rather than column, so a channel
    /// missing from either table cannot shift the others.
    /// </summary>
    private static Dictionary<int, (long Correctables, long Uncorrectables)> CodewordsByChannel(
        Dictionary<string, List<string>> table)
    {
        var result = new Dictionary<int, (long, long)>();
        var ids = Row(table, "Channel ID");
        for (int i = 0; i < ids.Count; i++)
        {
            var id = ParseInt(ids[i]);
            if (id > 0)
            {
                result.TryAdd(id, (
                    ParseLong(Cell(table, "Total Correctable Codewords", i)),
                    ParseLong(Cell(table, "Total Uncorrectable Codewords", i))));
            }
        }
        return result;
    }

    private static void ApplySb6141Status(string indexHtml, CableModemStats stats)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(indexHtml);

        foreach (var row in doc.DocumentNode.SelectNodes("//tr") ?? Enumerable.Empty<HtmlNode>())
        {
            var cells = row.SelectNodes("./td");
            if (cells == null || cells.Count != 2)
                continue;

            var label = CleanCell(cells[0]);
            var value = CleanCell(cells[1]);
            if (label.Equals("Cable Modem Status", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
                stats.DocsisState = new CmDocsisState(Mode: null, State: value, ReinitReason: null);
            else if (label.Equals("System Up Time", StringComparison.OrdinalIgnoreCase))
                stats.UptimeSeconds = ParseSb6141Uptime(value);
        }
    }

    /// <summary>
    /// The SB6141's uptime ("38 days 23h:34m:40s") in seconds; null for any other shape.
    /// </summary>
    internal static long? ParseSb6141Uptime(string text)
    {
        var match = Sb6141UptimeRx.Match(CleanText(text));
        if (!match.Success)
            return null;

        var days = match.Groups[1].Success ? long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        return days * 86400
            + long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 3600
            + long.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) * 60
            + long.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The SB6141 lists every burst profile's modulation ("[3] QPSK [1] 16QAM [2] 64QAM"). Keep the
    /// highest order, the one data traffic uses; unrecognized text passes through, empty is null.
    /// </summary>
    internal static string? HighestOrderModulation(string text)
    {
        string? best = null;
        var bestOrder = 0;
        foreach (Match m in UpstreamModulationRx.Matches(text))
        {
            var order = m.Groups[1].Success
                ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
                : m.Value.Equals("QPSK", StringComparison.OrdinalIgnoreCase) ? 4 : 2;
            if (order > bestOrder)
            {
                bestOrder = order;
                best = m.Value;
            }
        }

        return best ?? (string.IsNullOrWhiteSpace(text) ? null : text);
    }

    /// <summary>
    /// Symbol rate in symbols per second ("5.120 Msym/sec" is 5,120,000).
    /// </summary>
    private static long ParseSymbolRatePerSecond(string text)
    {
        var value = ParseDouble(text);
        if (value is not { } rate)
            return 0;

        var scale = text.Contains("Msym", StringComparison.OrdinalIgnoreCase) ? 1_000_000
            : text.Contains("Ksym", StringComparison.OrdinalIgnoreCase) ? 1_000
            : 1;
        return (long)Math.Round(rate * scale);
    }

    private static string CleanCell(HtmlNode node) => CleanText(node.InnerText);

    /// <summary>
    /// Decode entities and collapse whitespace. Every SB6141 value cell ends in &amp;nbsp;, which
    /// the numeric parsers would otherwise reject.
    /// </summary>
    private static string CleanText(string text) =>
        Regex.Replace(HtmlEntity.DeEntitize(text).Replace(' ', ' '), @"\s+", " ").Trim();

    private void ParseDownstreamTable(HtmlNode table, CableModemStats stats)
    {
        var rows = table.SelectNodes(".//tr[position()>1]");
        if (rows == null) return;

        foreach (var row in rows)
        {
            var cells = row.SelectNodes("td");
            if (cells == null || cells.Count < 8) continue;

            // Skip header-like rows
            var firstCell = cells[0].InnerText.Trim();
            if (!int.TryParse(firstCell, out _) &&
                !firstCell.Contains("Channel", StringComparison.OrdinalIgnoreCase))
                continue;

            var channel = new DsChannel
            {
                ChannelId = ParseInt(cells[0].InnerText),
                LockStatus = cells[1].InnerText.Trim(),
                Modulation = cells[2].InnerText.Trim(),
                Frequency = ParseFrequency(cells[3].InnerText),
                Power = ParseDouble(cells[4].InnerText),
                Snr = ParseDouble(cells[5].InnerText),
                Correctables = ParseLong(cells[6].InnerText),
                Uncorrectables = ParseLong(cells[7].InnerText),
            };

            if (channel.ChannelId > 0 || !string.IsNullOrWhiteSpace(channel.LockStatus))
                stats.DownstreamChannels.Add(channel);
        }
    }

    private void ParseUpstreamTable(HtmlNode table, CableModemStats stats)
    {
        var rows = table.SelectNodes(".//tr[position()>1]");
        if (rows == null) return;

        foreach (var row in rows)
        {
            var cells = row.SelectNodes("td");
            if (cells == null || cells.Count < 5) continue;

            // Skip header-like rows
            var firstCell = cells[0].InnerText.Trim();
            if (!int.TryParse(firstCell, out _) &&
                !firstCell.Contains("Channel", StringComparison.OrdinalIgnoreCase))
                continue;

            var channel = new UsChannel
            {
                ChannelId = ParseInt(cells[0].InnerText),
                LockStatus = cells[1].InnerText.Trim(),
                ChannelType = cells[2].InnerText.Trim(),
                Frequency = ParseFrequency(cells[3].InnerText),
                Power = ParseDouble(cells[4].InnerText) ?? 0,
            };

            if (channel.ChannelId > 0 || !string.IsNullOrWhiteSpace(channel.LockStatus))
                stats.UpstreamChannels.Add(channel);
        }
    }

    /// <summary>
    /// Fix malformed HTML from SB8200: remove extra &lt;/tr&gt; tags after "Bonded Channels" text.
    /// </summary>
    private static string FixMalformedHtml(string html)
    {
        // The SB8200 sometimes emits an extra </tr> between the header row and data rows
        // right after "Bonded Channels" text, which breaks table parsing.
        return html.Replace("Bonded Channels</td></tr></tr>", "Bonded Channels</td></tr>")
                   .Replace("Bonded Channels</td>\n</tr>\n</tr>", "Bonded Channels</td>\n</tr>");
    }

    private HttpClient CreateHttpClient(IDeviceDialer dialer, bool ignoreSslErrors)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = DeviceHttp.Via(dialer),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        if (ignoreSslErrors)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
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
        return double.TryParse(cleaned, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : null;
    }

    private static long ParseFrequency(string text)
    {
        var cleaned = StripUnits(text);
        return long.TryParse(cleaned, out var val) ? val : 0;
    }

    /// <summary>
    /// Remove common unit suffixes from cable modem values.
    /// </summary>
    private static string StripUnits(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var cleaned = text.Trim();

        string[] units = { "Ksym/sec", "Msym/sec", "dBmV", "dB", "Hz" };
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
        _tokenCache.Clear();
        _sb6141Configs.Clear();
    }
}
