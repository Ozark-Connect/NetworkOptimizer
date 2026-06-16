using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
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

    // Some Netgear modems (e.g. CM600) allow only one web session at a time. When another
    // session is active, any page 302-redirects to MultiLogin.asp, which offers to log out
    // the existing session. These patterns pull the takeover token and RetailSessionId from
    // that page so polling can claim the session instead of failing.
    private static readonly Regex MultiLoginSessionRx =
        new(@"goform/MultiLogin\?session=([0-9A-Za-z]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RetailSessionRx =
        new("name=[\"']?RetailSessionId[\"']?\\s+value=[\"']?([0-9A-Za-z]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
        // Follow redirects manually (AllowAutoRedirect = false) so the Basic auth header is
        // sent on EVERY hop. .NET drops the Authorization header when auto-following a
        // redirect, which breaks single-session modems (e.g. CM600) that 302 every page to
        // MultiLogin.asp when another session is active - the redirected request would 401.
        // The cookie jar keeps the session across the takeover (GET -> POST -> re-GET).
        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            CookieContainer = new System.Net.CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = false,
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

        var response = await GetFollowingRedirectsAsync(client, url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        // If we landed on the single-session takeover page (rather than the status page),
        // claim the session and re-fetch. Modems without this behavior never hit this path,
        // so the normal flow is unchanged.
        if (IsMultiLoginPage(response.RequestMessage?.RequestUri, content))
        {
            if (await TryTakeOverSessionAsync(client, url, content, cancellationToken))
            {
                response = await GetFollowingRedirectsAsync(client, url, cancellationToken);
                response.EnsureSuccessStatusCode();
                content = await response.Content.ReadAsStringAsync(cancellationToken);
            }
        }

        return string.IsNullOrWhiteSpace(content) ? null : content;
    }

    /// <summary>
    /// GET a URL, following same-style redirects manually so the client's default headers
    /// (notably Basic auth) are re-sent on each hop - unlike HttpClient auto-redirect, which
    /// strips the Authorization header. Resolves relative Location values (e.g.
    /// "MultiLogin.asp"). Stops after a few hops to avoid loops.
    /// </summary>
    private static async Task<HttpResponseMessage> GetFollowingRedirectsAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        var current = url;
        var response = await client.GetAsync(current, cancellationToken);

        for (int hop = 0; hop < 5
            && (int)response.StatusCode is >= 300 and < 400
            && response.Headers.Location != null; hop++)
        {
            var next = new Uri(new Uri(current), response.Headers.Location);
            response.Dispose();
            current = next.ToString();
            response = await client.GetAsync(current, cancellationToken);
        }

        return response;
    }

    /// <summary>
    /// True when a response is the Netgear single-session "MultiLogin" takeover page
    /// (another web session is active) rather than the requested status page.
    /// </summary>
    private static bool IsMultiLoginPage(Uri? finalUri, string content)
    {
        if (finalUri != null && finalUri.AbsolutePath.Contains("MultiLogin", StringComparison.OrdinalIgnoreCase))
            return true;
        return content.Contains("goform/MultiLogin", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Logs out the existing web session by POSTing the MultiLogin takeover form
    /// (<c>yes=yes&amp;Act=yes&amp;RetailSessionId=...</c> to
    /// <c>/goform/MultiLogin?session=...</c>). Returns true if the takeover was submitted.
    /// </summary>
    private async Task<bool> TryTakeOverSessionAsync(
        HttpClient client,
        string statusUrl,
        string multiLoginHtml,
        CancellationToken cancellationToken)
    {
        var sessionMatch = MultiLoginSessionRx.Match(multiLoginHtml);
        var retailMatch = RetailSessionRx.Match(multiLoginHtml);
        if (!sessionMatch.Success || !retailMatch.Success)
        {
            _logger.LogDebug("Netgear CM MultiLogin page seen but takeover tokens not found; cannot reclaim session");
            return false;
        }

        var authority = new Uri(statusUrl).GetLeftPart(UriPartial.Authority);
        var postUrl = $"{authority}/goform/MultiLogin?session={sessionMatch.Groups[1].Value}";
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("yes", "yes"),
            new KeyValuePair<string, string>("Act", "yes"),
            new KeyValuePair<string, string>("RetailSessionId", retailMatch.Groups[1].Value),
        });

        _logger.LogDebug("Netgear CM: another web session active; logging it out via MultiLogin to poll");
        var postResponse = await client.PostAsync(postUrl, form, cancellationToken);
        // The takeover POST returns a 302 redirect on success (we don't auto-follow it; the
        // caller re-fetches the status page). Treat any non-error status as accepted.
        return (int)postResponse.StatusCode < 400;
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
