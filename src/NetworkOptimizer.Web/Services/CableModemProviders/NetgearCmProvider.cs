using System.Net.Http.Headers;
using System.Text;
using HtmlAgilityPack;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;

namespace NetworkOptimizer.Web.Services.CableModemProviders;

/// <summary>
/// Cable modem provider for Netgear DOCSIS modems (CM1000, CM1100, CM1200, etc.).
/// Scrapes the DocsisStatus.asp page via HTTP Basic Auth and parses
/// downstream/upstream channel tables using HtmlAgilityPack.
/// </summary>
public sealed class NetgearCmProvider : ICableModemProvider
{
    /// <inheritdoc/>
    public string ProviderKey => "netgear";

    /// <inheritdoc/>
    public string DisplayName => "Netgear CM (HTTP)";

    private const string DefaultStatusPath = "/DocsisStatus.asp";
    private const int TimeoutSeconds = 15;
    private const int MaxRetries = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly ILogger<NetgearCmProvider> _logger;

    public NetgearCmProvider(ILogger<NetgearCmProvider> logger)
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
            _logger.LogWarning("Netgear CM poll requested but Host is empty (config {Id})", context.Id);
            return null;
        }

        var url = BuildUrl(context);

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var html = await FetchPageAsync(url, context.Username, context.Password, cancellationToken);
                if (html == null)
                {
                    _logger.LogWarning(
                        "Netgear CM at {Host} returned empty response (attempt {Attempt}/{Max})",
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
                    "Netgear CM {Name} polled: {DsCount} DS channels, {UsCount} US channels",
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
                    ex, "Transient error polling Netgear CM {Name} (attempt {Attempt}/{Max})",
                    context.Name, attempt, MaxRetries);
                await Task.Delay(RetryDelay, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error polling Netgear CM {Name} at {Host}", context.Name, context.Host);
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

        var url = BuildUrl(context);

        try
        {
            var html = await FetchPageAsync(url, context.Username, context.Password, cancellationToken);
            if (html == null)
                return (false, "No response from cable modem");

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var dsTable = doc.DocumentNode.SelectSingleNode("//table[@id='dsTable']");
            var usTable = doc.DocumentNode.SelectSingleNode("//table[@id='usTable']");

            if (dsTable == null && usTable == null)
                return (false, "Connected but DOCSIS status tables not found. Check the status page path.");

            var dsRows = dsTable?.SelectNodes(".//tr[position()>1]")?.Count ?? 0;
            var usRows = usTable?.SelectNodes(".//tr[position()>1]")?.Count ?? 0;

            return (true, $"Connected - {dsRows} downstream, {usRows} upstream channels detected");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return (false, "Authentication failed - check username/password");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    private string BuildUrl(CmPollContext context)
    {
        var path = string.IsNullOrWhiteSpace(context.StatusPagePath)
            ? DefaultStatusPath
            : context.StatusPagePath;

        var port = context.Port > 0 ? context.Port : 80;
        var portSuffix = port == 80 ? "" : $":{port}";

        return $"http://{context.Host}{portSuffix}{path}";
    }

    private async Task<string?> FetchPageAsync(
        string url,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
        };

        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        var response = await client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(content) ? null : content;
    }

    private CableModemStats ParseDocsisStatus(string html, CmPollContext context)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var stats = new CableModemStats
        {
            Timestamp = DateTime.UtcNow,
            DeviceHost = context.Host,
            DeviceName = context.Name,
            DeviceModel = "Netgear",
        };

        // Parse downstream table using header-based column mapping
        var dsTable = doc.DocumentNode.SelectSingleNode("//table[@id='dsTable']");
        if (dsTable != null)
        {
            var rows = dsTable.SelectNodes(".//tr");
            if (rows is { Count: >= 2 })
            {
                var headerCells = rows[0].SelectNodes(".//td | .//th");
                var headers = headerCells?.Select(c => NormalizeHeader(c.InnerText)).ToList() ?? new();

                for (int i = 1; i < rows.Count; i++)
                {
                    var cells = rows[i].SelectNodes(".//td");
                    if (cells == null || cells.Count != headers.Count) continue;

                    var channel = new DsChannel();
                    for (int j = 0; j < headers.Count; j++)
                    {
                        var val = cells[j].InnerText.Trim();
                        switch (headers[j])
                        {
                            case "channel": channel.ChannelId = ParseInt(val); break;
                            case "channelid": channel.ChannelId = ParseInt(val); break;
                            case "lockstatus": channel.LockStatus = val; break;
                            case "modulation": channel.Modulation = val; break;
                            case "frequency": channel.Frequency = ParseFrequency(val); break;
                            case "power": channel.Power = ParseDouble(val); break;
                            case "snr": case "snr/mer": channel.Snr = ParseDouble(val); break;
                            case "correctables": case "corrected": channel.Correctables = ParseLong(val); break;
                            case "uncorrectables": channel.Uncorrectables = ParseLong(val); break;
                        }
                    }
                    stats.DownstreamChannels.Add(channel);
                }
            }
        }

        // Parse upstream table using header-based column mapping
        var usTable = doc.DocumentNode.SelectSingleNode("//table[@id='usTable']");
        if (usTable != null)
        {
            var rows = usTable.SelectNodes(".//tr");
            if (rows is { Count: >= 2 })
            {
                var headerCells = rows[0].SelectNodes(".//td | .//th");
                var headers = headerCells?.Select(c => NormalizeHeader(c.InnerText)).ToList() ?? new();

                for (int i = 1; i < rows.Count; i++)
                {
                    var cells = rows[i].SelectNodes(".//td");
                    if (cells == null || cells.Count != headers.Count) continue;

                    var channel = new UsChannel();
                    for (int j = 0; j < headers.Count; j++)
                    {
                        var val = cells[j].InnerText.Trim();
                        switch (headers[j])
                        {
                            case "channel": channel.ChannelId = ParseInt(val); break;
                            case "channelid": channel.ChannelId = ParseInt(val); break;
                            case "lockstatus": channel.LockStatus = val; break;
                            case "uschanneltype": case "channeltype": channel.ChannelType = val; break;
                            case "frequency": channel.Frequency = ParseFrequency(val); break;
                            case "power": channel.Power = ParseDouble(val); break;
                            case "symbolrate": channel.SymbolRate = ParseSymbolRate(val); break;
                        }
                    }
                    stats.UpstreamChannels.Add(channel);
                }
            }
        }

        return stats;
    }

    /// <summary>
    /// Normalize header text for matching: lowercase, strip whitespace, slashes, and non-alpha chars.
    /// E.g. "Lock Status" → "lockstatus", "SNR/MER" → "snr/mer", "Channel ID" → "channelid"
    /// </summary>
    private static string NormalizeHeader(string text)
    {
        return text.Trim().Replace(" ", "").Replace("\n", "").Replace("\r", "").ToLowerInvariant();
    }

    /// <summary>
    /// Parse integer from text, stripping any non-numeric content.
    /// </summary>
    private static int ParseInt(string text)
    {
        var cleaned = StripUnits(text);
        return int.TryParse(cleaned, out var val) ? val : 0;
    }

    /// <summary>
    /// Parse long from text, stripping any non-numeric content.
    /// </summary>
    private static long ParseLong(string text)
    {
        var cleaned = StripUnits(text);
        return long.TryParse(cleaned, out var val) ? val : 0;
    }

    /// <summary>
    /// Parse double from text, stripping units like dBmV, dB.
    /// </summary>
    private static double? ParseDouble(string text)
    {
        var cleaned = StripUnits(text);
        return double.TryParse(cleaned, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : null;
    }

    /// <summary>
    /// Parse frequency value, stripping " Hz" suffix and returning as long (Hz).
    /// </summary>
    private static long ParseFrequency(string text)
    {
        var cleaned = StripUnits(text);
        return long.TryParse(cleaned, out var val) ? val : 0;
    }

    /// <summary>
    /// Parse symbol rate, stripping " Ksym/sec" suffix.
    /// </summary>
    private static long ParseSymbolRate(string text)
    {
        var cleaned = StripUnits(text);
        return long.TryParse(cleaned, out var val) ? val : 0;
    }

    /// <summary>
    /// Remove common unit suffixes from cable modem values.
    /// Handles: dBmV, dB, Hz, Ksym/sec, and whitespace.
    /// </summary>
    private static string StripUnits(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var cleaned = text.Trim();

        // Remove common unit suffixes (order matters - longer first)
        string[] units = { "Ksym/sec", "dBmV", "dB", "Hz", "Msym/sec" };
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
