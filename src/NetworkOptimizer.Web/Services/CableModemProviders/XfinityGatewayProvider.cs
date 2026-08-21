using System.Collections.Concurrent;
using System.Net;
using HtmlAgilityPack;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.Monitoring;

namespace NetworkOptimizer.Web.Services.CableModemProviders;

/// <summary>
/// Cable modem provider for gateways sharing the Comcast-derived .jst web UI,
/// whatever the OEM: Xfinity XB8/XB10 (Sercomm), Comcast Business CGA4332
/// (Technicolor), Cox CGM4981 (Technicolor). Authenticates via form POST to
/// /check.jst, then scrapes DOCSIS channel tables that use a transposed layout
/// where each row is a metric and each column is a channel.
///
/// Firmware families differ in two ways this provider absorbs rather than
/// exposing as separate providers, because a user cannot tell them apart from
/// the outside: residential builds serve the tables from /network_setup.jst and
/// label the channel row "Channel ID", while Comcast Business builds serve
/// /comcast_network.jst and label it "Index".
///
/// Not to be confused with <see cref="TechnicolorCgaProvider"/>. That one talks
/// to Technicolor's own firmware over its JSON API; the CGA hardware here runs
/// Comcast's UI instead and shares none of that transport.
/// </summary>
public sealed class XfinityGatewayProvider : ICableModemProvider
{
    /// <inheritdoc/>
    public string ProviderKey => "xfinity-gateway";

    /// <inheritdoc/>
    public string DisplayName => "Xfinity XB8/XB10, Comcast Business CGA4332, Cox CGM4981 (HTTP)";

    /// <summary>
    /// Status page candidates in discovery order. Residential firmware first:
    /// it is the larger population, so most sites settle on one request.
    /// </summary>
    private static readonly string[] DefaultStatusPaths =
    {
        "/network_setup.jst",
        "/comcast_network.jst",
    };

    private const string LoginPath = "/check.jst";
    private const int TimeoutSeconds = 15;
    private const int MaxRetries = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly ILogger<XfinityGatewayProvider> _logger;

    /// <summary>
    /// Status page path discovered per configured modem, so only the first poll
    /// pays for probing. Keyed by site and configuration ID together: this
    /// provider is a singleton shared by every site, and configuration IDs only
    /// count within one site's database.
    /// </summary>
    private readonly ConcurrentDictionary<string, DiscoveredPath> _discoveredPaths = new();

    /// <summary>
    /// A remembered discovery. <paramref name="ConfiguredPath"/> is the override
    /// in force when it was made, so editing that setting re-discovers instead of
    /// serving a stale answer.
    /// </summary>
    private sealed record DiscoveredPath(string? ConfiguredPath, string Path);

    public XfinityGatewayProvider(ILogger<XfinityGatewayProvider> logger)
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
            _logger.LogWarning("Xfinity Gateway poll requested but Host is empty (config {Id})", context.Id);
            return PollResult<CableModemStats>.Failed("No address is configured for this device.");
        }

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var html = await FetchStatusPageAsync(context, cancellationToken);
                if (html == null)
                {
                    _logger.LogWarning(
                        "Xfinity Gateway at {Host} returned empty response (attempt {Attempt}/{Max})",
                        context.ConfiguredHost ?? context.Host, attempt, MaxRetries);
                    if (attempt < MaxRetries)
                    {
                        await Task.Delay(RetryDelay, cancellationToken);
                        continue;
                    }
                    return PollResult<CableModemStats>.Failed($"No stats could be read from {(context.ConfiguredHost ?? context.Host)}.");
                }

                var stats = ParseNetworkSetup(html, context);
                _logger.LogDebug(
                    "Xfinity Gateway {Name} polled: {DsCount} DS channels, {UsCount} US channels",
                    context.Name, stats.DownstreamChannels.Count, stats.UpstreamChannels.Count);
                return PollResult<CableModemStats>.Ok(stats);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries)
            {
                _logger.LogDebug(
                    ex, "Transient error polling Xfinity Gateway {Name} (attempt {Attempt}/{Max})",
                    context.Name, attempt, MaxRetries);
                await Task.Delay(RetryDelay, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error polling Xfinity Gateway {Name} at {Host}", context.Name, context.ConfiguredHost ?? context.Host);
                return PollResult<CableModemStats>.Failed(HttpFailureSummary.Describe(ex, (context.ConfiguredHost ?? context.Host)));
            }
        }

        // Every retry is spent. The last attempt's own catch returns before this,
        // so reaching here means each one came back empty rather than throwing.
        return PollResult<CableModemStats>.Failed(
            $"No stats could be read from {context.ConfiguredHost ?? context.Host}.");
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
            var html = await FetchStatusPageAsync(context, cancellationToken);
            if (html == null)
                return (false, "No response from gateway - check host and credentials");

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var tables = FindChannelTables(doc);
            if (tables.Downstream == null && tables.Upstream == null)
                return (false, "Connected but DOCSIS channel tables not found. Is this an Xfinity gateway?");

            var dsCount = CountChannelsInTransposedTable(tables.Downstream);
            var usCount = CountChannelsInTransposedTable(tables.Upstream);

            var model = ExtractProductType(doc);
            var modelSuffix = string.IsNullOrEmpty(model) ? "" : $" ({model})";

            return (true, $"Connected{modelSuffix} - {dsCount} downstream, {usCount} upstream channels detected");
        }
        catch (Exception ex)
        {
            return (false, HttpFailureSummary.Describe(ex, context.ConfiguredHost ?? context.Host));
        }
    }

    /// <summary>
    /// Authenticate via form POST, then fetch the first status page that yields
    /// DOCSIS channels. The winning path is remembered for this modem, so the
    /// probing cost is paid once rather than on every poll.
    /// </summary>
    private async Task<string?> FetchStatusPageAsync(CmPollContext context, CancellationToken cancellationToken)
    {
        var port = context.Port > 0 ? context.Port : 80;
        var portSuffix = port == 80 ? "" : $":{port}";
        var baseUrl = $"http://{context.Host}{portSuffix}";

        using var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
        };

        var username = context.Username ?? "admin";
        var password = context.Password ?? "";
        var loginContent = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("username", username),
            new KeyValuePair<string, string>("password", password),
        });

        var loginResponse = await client.PostAsync($"{baseUrl}{LoginPath}", loginContent, cancellationToken);

        if (!loginResponse.IsSuccessStatusCode)
        {
            _logger.LogDebug("Xfinity Gateway login returned {Status} for {Host}",
                loginResponse.StatusCode, context.ConfiguredHost ?? context.Host);
            return null;
        }

        // Keep the first page that authenticated but had no tables: if no
        // candidate parses, returning it lets the caller say "connected, wrong
        // page" instead of the indistinguishable "no response".
        string? readableButUnparsed = null;

        foreach (var path in CandidatePaths(context))
        {
            var html = await TryGetPageAsync(client, baseUrl, path, context, cancellationToken);
            if (html == null)
                continue;

            readableButUnparsed ??= html;

            if (!HasChannelData(html))
            {
                _logger.LogDebug("Xfinity Gateway at {Host}: {Path} has no DOCSIS channel tables",
                    context.ConfiguredHost ?? context.Host, path);
                continue;
            }

            Remember(context, path);
            return html;
        }

        return readableButUnparsed;
    }

    /// <summary>
    /// GET one candidate path. A 404 or a bounce back to the login page is an
    /// ordinary "not this one" answer here, not a failure worth surfacing.
    /// </summary>
    private async Task<string?> TryGetPageAsync(
        HttpClient client,
        string baseUrl,
        string path,
        CmPollContext context,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync($"{baseUrl}{path}", cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Xfinity Gateway at {Host}: {Path} could not be fetched",
                context.ConfiguredHost ?? context.Host, path);
            return null;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Xfinity Gateway at {Host}: {Path} returned {Status}",
                    context.ConfiguredHost ?? context.Host, path, response.StatusCode);
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(html))
                return null;

            if (IsLoginPage(html))
            {
                _logger.LogDebug("Xfinity Gateway at {Host}: login failed, got redirected back to login page",
                    context.ConfiguredHost ?? context.Host);
                return null;
            }

            return html;
        }
    }

    /// <summary>
    /// Paths to try, best guess first: what worked last time, then any explicit
    /// override, then the built-in candidates.
    /// </summary>
    internal IEnumerable<string> CandidatePaths(CmPollContext context)
    {
        var configured = string.IsNullOrWhiteSpace(context.StatusPagePath) ? null : context.StatusPagePath;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_discoveredPaths.TryGetValue(CacheKey(context), out var remembered)
            && string.Equals(remembered.ConfiguredPath, configured, StringComparison.OrdinalIgnoreCase)
            && seen.Add(remembered.Path))
        {
            yield return remembered.Path;
        }

        if (configured != null && seen.Add(configured))
            yield return configured;

        foreach (var path in DefaultStatusPaths)
        {
            if (seen.Add(path))
                yield return path;
        }
    }

    internal void Remember(CmPollContext context, string path)
    {
        var configured = string.IsNullOrWhiteSpace(context.StatusPagePath) ? null : context.StatusPagePath;
        var entry = new DiscoveredPath(configured, path);
        var key = CacheKey(context);

        if (_discoveredPaths.TryGetValue(key, out var previous) && previous == entry)
            return;

        _discoveredPaths[key] = entry;
        _logger.LogDebug("Xfinity Gateway {Name} at {Host}: serving DOCSIS stats from {Path}",
            context.Name, context.ConfiguredHost ?? context.Host, path);
    }

    private static string CacheKey(CmPollContext context) => $"{context.SiteSlug}/{context.Id}";

    /// <summary>Whether a page carries a channel table this provider can read.</summary>
    private static bool HasChannelData(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var tables = FindChannelTables(doc);
        return CountChannelsInTransposedTable(tables.Downstream) > 0
            || CountChannelsInTransposedTable(tables.Upstream) > 0;
    }

    /// <summary>
    /// Detect if the response is the login page rather than the status page.
    /// The login page POSTs to check.jst.
    /// </summary>
    private static bool IsLoginPage(string html)
    {
        return html.Contains("action=\"check.jst\"", StringComparison.OrdinalIgnoreCase)
            || html.Contains("action='/check.jst'", StringComparison.OrdinalIgnoreCase);
    }

    internal CableModemStats ParseNetworkSetup(string html, CmPollContext context)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var stats = new CableModemStats
        {
            Timestamp = DateTime.UtcNow,
            DeviceHost = context.ConfiguredHost ?? context.Host,
            DeviceName = context.Name,
            DeviceModel = ExtractProductType(doc) ?? "Xfinity Gateway",
        };

        var tables = FindChannelTables(doc);

        if (tables.Downstream != null)
            ParseTransposedDownstreamTable(tables.Downstream, stats);

        if (tables.Upstream != null)
            ParseTransposedUpstreamTable(tables.Upstream, stats);

        if (tables.ErrorCodewords != null)
            MergeErrorCodewords(tables.ErrorCodewords, stats);

        return stats;
    }

    /// <summary>
    /// Locate the three DOCSIS tables by their header text.
    /// Tables are inside div.netFlow containers with thead text identifying them.
    /// </summary>
    private static (HtmlNode? Downstream, HtmlNode? Upstream, HtmlNode? ErrorCodewords) FindChannelTables(
        HtmlDocument doc)
    {
        HtmlNode? dsTable = null, usTable = null, errTable = null;

        var tables = doc.DocumentNode.SelectNodes("//table[contains(@class,'data')]");
        if (tables == null) return (null, null, null);

        foreach (var table in tables)
        {
            var headerText = table.SelectSingleNode(".//thead")?.InnerText ?? "";

            if (headerText.Contains("Downstream", StringComparison.OrdinalIgnoreCase)
                && headerText.Contains("Channel Bonding", StringComparison.OrdinalIgnoreCase))
            {
                dsTable = table;
            }
            else if (headerText.Contains("Upstream", StringComparison.OrdinalIgnoreCase)
                     && headerText.Contains("Channel Bonding", StringComparison.OrdinalIgnoreCase))
            {
                usTable = table;
            }
            else if (headerText.Contains("Error Codewords", StringComparison.OrdinalIgnoreCase))
            {
                errTable = table;
            }
        }

        return (dsTable, usTable, errTable);
    }

    /// <summary>
    /// Parse a transposed downstream table where each row is a metric
    /// and each column is a channel.
    /// Rows: Channel ID, Lock Status, Frequency, SNR, Power Level, Modulation.
    /// </summary>
    private void ParseTransposedDownstreamTable(HtmlNode table, CableModemStats stats)
    {
        var metricRows = ExtractMetricRows(table);
        var channelIds = FindChannelIdRow(metricRows);
        if (channelIds == null)
            return;

        metricRows.TryGetValue("lockstatus", out var lockStatuses);
        metricRows.TryGetValue("frequency", out var frequencies);
        metricRows.TryGetValue("snr", out var snrs);
        metricRows.TryGetValue("powerlevel", out var powers);
        metricRows.TryGetValue("modulation", out var modulations);

        for (int i = 0; i < channelIds.Count; i++)
        {
            var channel = new DsChannel
            {
                ChannelId = ParseInt(GetAt(channelIds, i)),
                LockStatus = GetAt(lockStatuses, i) ?? "",
                Frequency = ParseFrequencyWithUnits(GetAt(frequencies, i)),
                Snr = ParseDouble(GetAt(snrs, i)),
                Power = ParseDouble(GetAt(powers, i)),
                Modulation = GetAt(modulations, i) ?? "",
            };

            stats.DownstreamChannels.Add(channel);
        }
    }

    /// <summary>
    /// Parse a transposed upstream table.
    /// Rows: Channel ID, Lock Status, Frequency, Symbol Rate, Power Level, Modulation, Channel Type.
    /// </summary>
    private void ParseTransposedUpstreamTable(HtmlNode table, CableModemStats stats)
    {
        var metricRows = ExtractMetricRows(table);
        var channelIds = FindChannelIdRow(metricRows);
        if (channelIds == null)
            return;

        metricRows.TryGetValue("lockstatus", out var lockStatuses);
        metricRows.TryGetValue("frequency", out var frequencies);
        metricRows.TryGetValue("symbolrate", out var symbolRates);
        metricRows.TryGetValue("powerlevel", out var powers);
        metricRows.TryGetValue("modulation", out var modulations);
        metricRows.TryGetValue("channeltype", out var channelTypes);

        for (int i = 0; i < channelIds.Count; i++)
        {
            var channel = new UsChannel
            {
                ChannelId = ParseInt(GetAt(channelIds, i)),
                LockStatus = GetAt(lockStatuses, i) ?? "",
                Frequency = ParseFrequencyWithUnits(GetAt(frequencies, i)),
                SymbolRate = ParseLong(GetAt(symbolRates, i)),
                Power = ParseDouble(GetAt(powers, i)),
                ChannelType = GetAt(channelTypes, i) ?? GetAt(modulations, i) ?? "",
            };

            stats.UpstreamChannels.Add(channel);
        }
    }

    /// <summary>
    /// Merge error codewords from the separate table into existing DS channels.
    /// Matches by channel ID where the table carries one; Comcast Business
    /// firmware omits that row entirely, so there the columns are positional and
    /// line up with the downstream table read moments earlier.
    /// </summary>
    private void MergeErrorCodewords(HtmlNode table, CableModemStats stats)
    {
        var metricRows = ExtractMetricRows(table);

        metricRows.TryGetValue("correctablecodewords", out var correctables);
        metricRows.TryGetValue("uncorrectablecodewords", out var uncorrectables);
        if (correctables == null && uncorrectables == null)
            return;

        if (metricRows.TryGetValue("channelid", out var channelIds) && channelIds.Count > 0)
        {
            var dsLookup = new Dictionary<int, DsChannel>();
            foreach (var ch in stats.DownstreamChannels)
                dsLookup.TryAdd(ch.ChannelId, ch);

            for (int i = 0; i < channelIds.Count; i++)
            {
                var chId = ParseInt(GetAt(channelIds, i));
                if (chId > 0 && dsLookup.TryGetValue(chId, out var channel))
                {
                    channel.Correctables = ParseLong(GetAt(correctables, i));
                    channel.Uncorrectables = ParseLong(GetAt(uncorrectables, i));
                }
            }

            return;
        }

        var columns = Math.Max(correctables?.Count ?? 0, uncorrectables?.Count ?? 0);
        if (columns != stats.DownstreamChannels.Count)
        {
            _logger.LogDebug(
                "Xfinity Gateway: codeword table has {Columns} columns for {Channels} downstream channels, skipping merge",
                columns, stats.DownstreamChannels.Count);
            return;
        }

        for (int i = 0; i < stats.DownstreamChannels.Count; i++)
        {
            stats.DownstreamChannels[i].Correctables = ParseLong(GetAt(correctables, i));
            stats.DownstreamChannels[i].Uncorrectables = ParseLong(GetAt(uncorrectables, i));
        }
    }

    /// <summary>
    /// The row identifying each channel column. Residential firmware labels it
    /// "Channel ID" and gives the real DOCSIS ID; Comcast Business labels it
    /// "Index" and gives a 1-based position instead, which is all that firmware
    /// exposes.
    /// </summary>
    private static List<string>? FindChannelIdRow(Dictionary<string, List<string>> metricRows)
    {
        if (metricRows.TryGetValue("channelid", out var ids) && ids.Count > 0)
            return ids;

        if (metricRows.TryGetValue("index", out var indexes) && indexes.Count > 0)
            return indexes;

        return null;
    }

    /// <summary>
    /// Extract metric rows from a transposed table.
    /// Each tbody tr has a th.row-label with the metric name, followed by td values.
    /// Returns a dictionary keyed by normalized metric name.
    /// </summary>
    private static Dictionary<string, List<string>> ExtractMetricRows(HtmlNode table)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        var rows = table.SelectNodes(".//tbody/tr");
        if (rows == null) return result;

        foreach (var row in rows)
        {
            var header = row.SelectSingleNode("th");
            if (header == null) continue;

            var metricName = NormalizeHeader(header.InnerText);
            if (string.IsNullOrEmpty(metricName)) continue;

            var cells = row.SelectNodes("td");
            if (cells == null) continue;

            var values = cells
                .Select(c =>
                {
                    var div = c.SelectSingleNode(".//div[contains(@class,'netWidth')]");
                    return (div ?? c).InnerText.Trim();
                })
                .ToList();

            result[metricName] = values;
        }

        return result;
    }

    /// <summary>
    /// Name the gateway from the Device Information section. Product Type is the
    /// name users know ("XB10", "CBR"), with the model number as a fallback for
    /// firmware that omits it.
    /// </summary>
    private static string? ExtractProductType(HtmlDocument doc)
    {
        return ExtractDeviceInfoValue(doc, "Product Type")
            ?? ExtractDeviceInfoValue(doc, "Model");
    }

    /// <summary>
    /// Read one label/value pair out of the Device Information section.
    /// </summary>
    private static string? ExtractDeviceInfoValue(HtmlDocument doc, string labelText)
    {
        var labels = doc.DocumentNode.SelectNodes("//span[contains(@class,'readonlyLabel')]");
        if (labels == null) return null;

        foreach (var label in labels)
        {
            if (!label.InnerText.Contains(labelText, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = label.ParentNode?.SelectSingleNode(".//span[contains(@class,'value')]");
            if (value != null)
            {
                var text = value.InnerText.Trim();
                if (!string.IsNullOrEmpty(text))
                    return text;
            }
        }

        return null;
    }

    private static int CountChannelsInTransposedTable(HtmlNode? table)
    {
        if (table == null) return 0;
        return FindChannelIdRow(ExtractMetricRows(table))?.Count ?? 0;
    }

    private static string? GetAt(List<string>? list, int index)
    {
        return list != null && index < list.Count ? list[index] : null;
    }

    /// <summary>
    /// Normalize header text for matching: lowercase, strip whitespace.
    /// E.g. "Channel ID" -> "channelid", "Power Level" -> "powerlevel"
    /// </summary>
    private static string NormalizeHeader(string text)
    {
        return text.Trim().Replace(" ", "").Replace("\n", "").Replace("\r", "").ToLowerInvariant();
    }

    private static int ParseInt(string? text)
    {
        var cleaned = StripUnits(text);
        return int.TryParse(cleaned, out var val) ? val : 0;
    }

    private static long ParseLong(string? text)
    {
        var cleaned = StripUnits(text);
        return long.TryParse(cleaned, out var val) ? val : 0;
    }

    private static double? ParseDouble(string? text)
    {
        var cleaned = StripUnits(text);
        return double.TryParse(cleaned, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : null;
    }

    /// <summary>
    /// Parse frequency that may be in "957 MHz" format or raw Hz ("774000000").
    /// SC-QAM channels use "957 MHz", OFDM/OFDMA channels may use raw Hz.
    /// </summary>
    private static long ParseFrequencyWithUnits(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var trimmed = text.Trim();

        if (trimmed.EndsWith("MHz", StringComparison.OrdinalIgnoreCase))
        {
            var numPart = trimmed[..^3].Trim();
            if (double.TryParse(numPart, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var mhz))
                return (long)(mhz * 1_000_000);
            return 0;
        }

        if (trimmed.EndsWith("GHz", StringComparison.OrdinalIgnoreCase))
        {
            var numPart = trimmed[..^3].Trim();
            if (double.TryParse(numPart, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var ghz))
                return (long)(ghz * 1_000_000_000);
            return 0;
        }

        if (trimmed.EndsWith("Hz", StringComparison.OrdinalIgnoreCase))
        {
            var numPart = trimmed[..^2].Trim();
            if (long.TryParse(numPart, out var hz))
                return hz;
            return 0;
        }

        if (long.TryParse(trimmed, out var raw))
            return raw;

        return 0;
    }

    /// <summary>
    /// Remove common unit suffixes from cable modem values.
    /// </summary>
    private static string StripUnits(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var cleaned = text.Trim();

        string[] units = { "Ksym/sec", "Msym/sec", "dBmV", "dB", "MHz", "GHz", "Hz" };
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
