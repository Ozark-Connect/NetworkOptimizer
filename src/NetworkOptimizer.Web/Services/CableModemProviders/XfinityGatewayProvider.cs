using System.Net;
using HtmlAgilityPack;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;

namespace NetworkOptimizer.Web.Services.CableModemProviders;

/// <summary>
/// Cable modem provider for gateways sharing the Xfinity/Technicolor web UI (Xfinity XB8/XB10, Cox CGM4981).
/// Authenticates via form POST to /check.jst, then scrapes DOCSIS channel
/// tables from /network_setup.jst. The tables use a transposed layout where
/// each row is a metric and each column is a channel.
/// </summary>
public sealed class XfinityGatewayProvider : ICableModemProvider
{
    /// <inheritdoc/>
    public string ProviderKey => "xfinity-gateway";

    /// <inheritdoc/>
    public string DisplayName => "Xfinity Gateway (HTTP)";

    private const string DefaultStatusPath = "/network_setup.jst";
    private const string LoginPath = "/check.jst";
    private const int TimeoutSeconds = 15;
    private const int MaxRetries = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly ILogger<XfinityGatewayProvider> _logger;

    public XfinityGatewayProvider(ILogger<XfinityGatewayProvider> logger)
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
            _logger.LogWarning("Xfinity Gateway poll requested but Host is empty (config {Id})", context.Id);
            return null;
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
                    return null;
                }

                var stats = ParseNetworkSetup(html, context);
                _logger.LogDebug(
                    "Xfinity Gateway {Name} polled: {DsCount} DS channels, {UsCount} US channels",
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
                    ex, "Transient error polling Xfinity Gateway {Name} (attempt {Attempt}/{Max})",
                    context.Name, attempt, MaxRetries);
                await Task.Delay(RetryDelay, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error polling Xfinity Gateway {Name} at {Host}", context.Name, context.ConfiguredHost ?? context.Host);
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
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Authenticate via form POST and fetch the status page in one session.
    /// Uses CookieContainer to carry the session cookie from login to page fetch.
    /// </summary>
    private async Task<string?> FetchStatusPageAsync(CmPollContext context, CancellationToken cancellationToken)
    {
        var port = context.Port > 0 ? context.Port : 80;
        var portSuffix = port == 80 ? "" : $":{port}";
        var baseUrl = $"http://{context.Host}{portSuffix}";

        var statusPath = string.IsNullOrWhiteSpace(context.StatusPagePath)
            ? DefaultStatusPath
            : context.StatusPagePath;

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

        var response = await client.GetAsync($"{baseUrl}{statusPath}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("Xfinity Gateway status page returned {Status} for {Host}",
                response.StatusCode, context.ConfiguredHost ?? context.Host);
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

    /// <summary>
    /// Detect if the response is the login page rather than the status page.
    /// The login page POSTs to check.jst.
    /// </summary>
    private static bool IsLoginPage(string html)
    {
        return html.Contains("action=\"check.jst\"", StringComparison.OrdinalIgnoreCase)
            || html.Contains("action='/check.jst'", StringComparison.OrdinalIgnoreCase);
    }

    private CableModemStats ParseNetworkSetup(string html, CmPollContext context)
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
        if (!metricRows.TryGetValue("channelid", out var channelIds) || channelIds.Count == 0)
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
        if (!metricRows.TryGetValue("channelid", out var channelIds) || channelIds.Count == 0)
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
    /// Merge error codewords from the separate table into existing DS channels by channel ID.
    /// </summary>
    private void MergeErrorCodewords(HtmlNode table, CableModemStats stats)
    {
        var metricRows = ExtractMetricRows(table);
        if (!metricRows.TryGetValue("channelid", out var channelIds) || channelIds.Count == 0)
            return;

        metricRows.TryGetValue("correctablecodewords", out var correctables);
        metricRows.TryGetValue("uncorrectablecodewords", out var uncorrectables);

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
    /// Extract the Product Type from the Device Information section (e.g. "XB10").
    /// </summary>
    private static string? ExtractProductType(HtmlDocument doc)
    {
        var labels = doc.DocumentNode.SelectNodes("//span[contains(@class,'readonlyLabel')]");
        if (labels == null) return null;

        foreach (var label in labels)
        {
            if (!label.InnerText.Contains("Product Type", StringComparison.OrdinalIgnoreCase))
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
        var metricRows = ExtractMetricRows(table);
        return metricRows.TryGetValue("channelid", out var ids) ? ids.Count : 0;
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
