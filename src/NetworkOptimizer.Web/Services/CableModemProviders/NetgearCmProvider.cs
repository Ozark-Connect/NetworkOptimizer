using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;

namespace NetworkOptimizer.Web.Services.CableModemProviders;

/// <summary>
/// Cable modem provider for Netgear DOCSIS modems (CM600, CM700, CM1000, CM1200, etc.).
/// Scrapes the DocsisStatus status page via HTTP Basic Auth and parses
/// downstream/upstream channel tables using HtmlAgilityPack.
///
/// Netgear is inconsistent about the page extension: most models serve
/// <c>DocsisStatus.asp</c> (e.g. CM600, CM1000) while some serve <c>DocsisStatus.htm</c>
/// (e.g. CM700) and return 401/404 for the .asp path. Model number does not reliably
/// predict which (CM600 and CM700 are both DOCSIS 3.0 yet differ), so the provider tries
/// the configured/default path and transparently falls back to the alternate extension.
///
/// The two pages also differ in how channel data is delivered: the .asp page renders the
/// downstream/upstream tables server-side, while the .htm page ships empty tables and a
/// JavaScript tagValueList that the browser expands client-side. The parser handles both -
/// the server-rendered tables first, then the tagValueList for any table left empty.
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

    // CM700 (and similar) serve DocsisStatus.htm with empty channel tables that the browser
    // fills client-side from a JavaScript tagValueList: a leading channel count followed by
    // pipe-delimited per-channel fields. Capture the live single-quoted assignment for each
    // table (the placeholder/example assignment in the function is double-quoted and inside a
    // /* */ comment, so the single-quote match skips it).
    private static readonly Regex DsTagValueListRx =
        new(@"InitDsTableTagValue\s*\(\s*\)\s*\{.*?var\s+tagValueList\s*=\s*'([^']*)'",
            RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex UsTagValueListRx =
        new(@"InitUsTableTagValue\s*\(\s*\)\s*\{.*?var\s+tagValueList\s*=\s*'([^']*)'",
            RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly ILogger<NetgearCmProvider> _logger;

    /// <summary>
    /// Remembers the (status-page path, send-credentials) combination that last succeeded per
    /// CmConfiguration.Id so later polls go straight to it instead of re-probing the dead
    /// extension or wrong auth mode every interval.
    /// </summary>
    private readonly ConcurrentDictionary<int, (string Path, bool UseCreds)> _attemptCache = new();

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

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                // Force the full path/credential matrix only on the final retry, so a cached
                // combo's transient blip recovers via the earlier retries without probing
                // (and logging) every other combo.
                var html = await FetchWithFallbackAsync(context, cancellationToken, fullMatrix: attempt == MaxRetries);
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

        try
        {
            // A manual test should probe everything to find a working combo, so force the
            // full matrix regardless of any cached result.
            var html = await FetchWithFallbackAsync(context, cancellationToken, fullMatrix: true);
            if (html == null)
                return (false, "No response from cable modem");

            // Parse through the full pipeline so both the server-rendered table format
            // (.asp) and the JavaScript tagValueList format (.htm, e.g. CM700) are counted.
            var stats = ParseDocsisStatus(html, context);
            var dsCount = stats.DownstreamChannels.Count;
            var usCount = stats.UpstreamChannels.Count;

            if (dsCount == 0 && usCount == 0)
                return (false, "Connected but no DOCSIS channels found. Check the status page path.");

            return (true, $"Connected - {dsCount} downstream, {usCount} upstream channels detected");
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

    private static string BuildUrl(CmPollContext context, string path)
    {
        var port = context.Port > 0 ? context.Port : 80;
        var portSuffix = port == 80 ? "" : $":{port}";

        return $"http://{context.Host}{portSuffix}{path}";
    }

    /// <summary>
    /// Fetch the DOCSIS status page, transparently falling back across both Netgear page
    /// extensions AND whether credentials are sent. Tries the configured/default path with
    /// credentials first (modems like the CM600 gate the page behind HTTP Basic), then the
    /// alternate extension, then the same paths WITHOUT credentials - some modems (e.g. CM700)
    /// serve the page openly and 401 a request that presents unexpected credentials. Any HTTP
    /// status error or transport error advances to the next attempt; the last attempt's error
    /// propagates, so a genuine auth failure (every attempt rejected) still surfaces. The
    /// winning combination is cached per config so later polls go straight to it.
    /// </summary>
    private async Task<string?> FetchWithFallbackAsync(
        CmPollContext context,
        CancellationToken cancellationToken,
        bool fullMatrix)
    {
        var attempts = ResolveAttempts(context, fullMatrix);

        for (int i = 0; i < attempts.Count; i++)
        {
            var (path, useCreds) = attempts[i];
            var url = BuildUrl(context, path);
            var user = useCreds ? context.Username : null;
            var pass = useCreds ? context.Password : null;
            try
            {
                var html = await FetchPageAsync(url, user, pass, cancellationToken);
                if (context.Id > 0)
                    _attemptCache[context.Id] = (path, useCreds);
                return html;
            }
            catch (HttpRequestException ex) when (i < attempts.Count - 1)
            {
                // Advance on any HTTP status error (401/403/404/5xx) or transport error
                // (connection reset/closed, where StatusCode is null). Netgear firmware
                // signals an unavailable path or unwanted credentials inconsistently - the
                // CM700 returned 401, other models close the connection - so we don't gate on
                // a specific code.
                _logger.LogDebug(
                    "Netgear CM {Name}: {Path} (creds={UseCreds}) failed ({Reason}); trying next attempt",
                    context.Name, path, useCreds,
                    ex.StatusCode is { } status ? $"HTTP {(int)status}" : ex.Message);
            }
        }

        // Unreachable: the loop returns on the first success, and the last attempt's exception
        // is not caught here (the when-guard requires a remaining attempt) so it propagates to
        // PollAsync/TestConnectionAsync.
        return null;
    }

    /// <summary>
    /// Build the ordered list of (path, send-credentials) attempts: the configured/default
    /// path and the alternate Netgear extension (.asp ↔ .htm), each tried with credentials
    /// first (when configured) and then without. The attempt recorded as working for this
    /// config is moved to the front.
    /// </summary>
    private List<(string Path, bool UseCreds)> ResolveAttempts(CmPollContext context, bool fullMatrix)
    {
        // Fast path: a known-good combo is cached and we're not forcing a re-probe. Return only
        // that combo so a transient blip self-recovers on the next poll retry instead of probing
        // (and logging a 401 or connection error for) every other path/credential combo. The
        // poll loop forces the full matrix on its final retry, so a combo that has genuinely
        // stopped working is still re-discovered.
        if (!fullMatrix && context.Id > 0 && _attemptCache.TryGetValue(context.Id, out var cached))
            return new List<(string Path, bool UseCreds)> { cached };

        var configured = string.IsNullOrWhiteSpace(context.StatusPagePath)
            ? DefaultStatusPath
            : context.StatusPagePath;

        var paths = new List<string> { configured };
        var alternate = AlternateNetgearPath(configured);
        if (alternate != null)
            paths.Add(alternate);

        var hasCreds = !string.IsNullOrEmpty(context.Username) && !string.IsNullOrEmpty(context.Password);

        var attempts = new List<(string Path, bool UseCreds)>();
        // Credentialed attempts first - required by modems that gate the page behind Basic auth.
        if (hasCreds)
            attempts.AddRange(paths.Select(p => (p, true)));
        // Then anonymous attempts - for modems that serve openly and reject unexpected creds.
        attempts.AddRange(paths.Select(p => (p, false)));

        if (context.Id > 0 && _attemptCache.TryGetValue(context.Id, out var lastGood))
        {
            var idx = attempts.FindIndex(a => a.Path == lastGood.Path && a.UseCreds == lastGood.UseCreds);
            if (idx > 0)
            {
                attempts.RemoveAt(idx);
                attempts.Insert(0, lastGood);
            }
        }

        return attempts;
    }

    /// <summary>
    /// Returns the other Netgear DOCSIS status-page extension for a given path, or null if
    /// the path is not a recognized DocsisStatus page. CM600/CM1000 use <c>.asp</c>; CM700
    /// uses <c>.htm</c>, and model number does not reliably predict which.
    /// </summary>
    private static string? AlternateNetgearPath(string path)
    {
        if (path.EndsWith("DocsisStatus.asp", StringComparison.OrdinalIgnoreCase))
            return string.Concat(path.AsSpan(0, path.Length - 4), ".htm");
        if (path.EndsWith("DocsisStatus.htm", StringComparison.OrdinalIgnoreCase))
            return string.Concat(path.AsSpan(0, path.Length - 4), ".asp");
        return null;
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

    internal static CableModemStats ParseDocsisStatus(string html, CmPollContext context)
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

        // Models that build the tables client-side (e.g. CM700 on DocsisStatus.htm) leave the
        // server-rendered tables empty, so fall back to the JavaScript tagValueList data.
        ParseTagValueTables(html, stats);

        return stats;
    }

    /// <summary>
    /// Parse channel data from the JavaScript tagValueList assignments used by models that
    /// render the DOCSIS tables client-side (e.g. CM700). Only runs for tables the
    /// HtmlAgilityPack pass left empty, so it never overrides server-rendered data.
    /// </summary>
    private static void ParseTagValueTables(string html, CableModemStats stats)
    {
        if (stats.DownstreamChannels.Count == 0)
        {
            var match = DsTagValueListRx.Match(html);
            if (match.Success)
            {
                // Per channel: Channel, Lock Status, Modulation, Channel ID, Frequency, Power,
                // SNR, and (on firmware that reports them) Correctables, UnCorrectables.
                foreach (var f in ChunkTagValues(match.Groups[1].Value))
                {
                    stats.DownstreamChannels.Add(new DsChannel
                    {
                        LockStatus = f[1],
                        Modulation = f[2],
                        ChannelId = ParseInt(f[3]),
                        Frequency = ParseFrequency(f[4]),
                        Power = ParseDouble(f[5]),
                        Snr = ParseDouble(f[6]),
                        Correctables = f.Count > 7 ? ParseLong(f[7]) : 0,
                        Uncorrectables = f.Count > 8 ? ParseLong(f[8]) : 0,
                    });
                }
            }
        }

        if (stats.UpstreamChannels.Count == 0)
        {
            var match = UsTagValueListRx.Match(html);
            if (match.Success)
            {
                // Per channel: Channel, Lock Status, US Channel Type, Channel ID, Symbol Rate,
                // Frequency, Power.
                foreach (var f in ChunkTagValues(match.Groups[1].Value))
                {
                    stats.UpstreamChannels.Add(new UsChannel
                    {
                        LockStatus = f[1],
                        ChannelType = f[2],
                        ChannelId = ParseInt(f[3]),
                        SymbolRate = ParseSymbolRate(f[4]),
                        Frequency = ParseFrequency(f[5]),
                        Power = ParseDouble(f[6]),
                    });
                }
            }
        }
    }

    /// <summary>
    /// Split a Netgear tagValueList - a leading channel count followed by pipe-delimited
    /// per-channel fields - into one field list per channel. A trailing pipe leaves an empty
    /// token that is ignored. Returns nothing if the count is missing/invalid or the fields
    /// don't divide into per-channel groups of at least the seven shared columns.
    /// </summary>
    private static IEnumerable<List<string>> ChunkTagValues(string tagValueList)
    {
        var tokens = tagValueList.Split('|');
        if (tokens.Length < 2 || !int.TryParse(tokens[0].Trim(), out var count) || count <= 0)
            yield break;

        var fields = tokens.Skip(1).ToList();
        while (fields.Count > 0 && string.IsNullOrEmpty(fields[^1]))
            fields.RemoveAt(fields.Count - 1);

        var perChannel = fields.Count / count;
        if (perChannel < 7)
            yield break;

        for (int c = 0; c < count; c++)
        {
            var start = c * perChannel;
            if (start + perChannel > fields.Count)
                yield break;
            // Trim each field to mirror the server-rendered path (InnerText.Trim()). Lock
            // status is matched exactly, so stray whitespace would otherwise zero out every
            // locked count and null the power/SNR averages.
            yield return fields.GetRange(start, perChannel).Select(f => f.Trim()).ToList();
        }
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
