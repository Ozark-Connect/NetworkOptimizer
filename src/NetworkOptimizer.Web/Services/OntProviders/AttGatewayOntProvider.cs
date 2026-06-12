using System.Text.RegularExpressions;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;

namespace NetworkOptimizer.Web.Services.OntProviders;

/// <summary>
/// Scrapes AT&amp;T residential gateways (BGW210, BGW320-500/505) that expose
/// fiber stats via unauthenticated HTTP pages. No login required.
/// Tries port-based scheme first, falls back to the opposite with self-signed cert bypass.
/// </summary>
public class AttGatewayOntProvider : IOntProvider
{
    private readonly ILogger<AttGatewayOntProvider> _logger;

    private static readonly Regex DdmRegex = new(
        @"<h1>([^<]+?)&nbsp;&nbsp;Currently\s+(-?\d+)</h1>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex KeyValueRegex = new(
        @"<th\s+scope=""row""[^>]*>([^<]+)</th>\s*<td\s+class=""col2""[^>]*>([^<]*)</td>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex KeyValueLooseRegex = new(
        @"<th\s+scope=""row""[^>]*>([^<]+)</th>\s*\n?\s*<td[^>]*>([^<]*)</td>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public AttGatewayOntProvider(ILogger<AttGatewayOntProvider> logger)
    {
        _logger = logger;
    }

    public string ProviderKey => "att-gateway";
    public string DisplayName => "AT&T Gateway (HTTP)";

    public async Task<OntStats?> PollAsync(OntPollContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = CreateHttpClient();
            var baseUrl = await ResolveBaseUrlAsync(client, context, cancellationToken);
            if (baseUrl == null)
            {
                _logger.LogWarning("Failed to reach AT&T gateway at {Host} via HTTP or HTTPS", context.Host);
                return null;
            }

            var stats = new OntStats
            {
                Timestamp = DateTime.UtcNow,
                DeviceHost = context.Host,
                DeviceName = context.Name,
                DeviceModel = "AT&T Gateway"
            };

            var fiberHtml = await FetchPageAsync(client, $"{baseUrl}/cgi-bin/fiberstat.ha", cancellationToken);
            if (fiberHtml == null)
            {
                _logger.LogWarning("Failed to fetch fiberstat.ha from {Host}", context.Host);
                return null;
            }

            ParseFiberStat(fiberHtml, stats);

            var broadbandHtml = await FetchPageAsync(client, $"{baseUrl}/cgi-bin/broadbandstatistics.ha", cancellationToken);
            if (broadbandHtml != null)
            {
                ParseBroadbandStatistics(broadbandHtml, stats);
            }

            DerivePonTypeFromWavelength(stats);

            _logger.LogDebug(
                "AT&T gateway {Host} polled: Rx={RxPower} dBm, Tx={TxPower} dBm, {PonType}, BWP={Bwp} Mbps",
                context.Host, stats.RxPowerDbm, stats.TxPowerDbm, stats.PonType, stats.BwpSpeedMbps);

            return stats;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error polling AT&T gateway at {Host}", context.Host);
            return null;
        }
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(
        OntPollContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = CreateHttpClient();
            var baseUrl = await ResolveBaseUrlAsync(client, context, cancellationToken);
            if (baseUrl == null)
                return (false, $"Could not reach {context.Host} via HTTP or HTTPS");

            var url = $"{baseUrl}/cgi-bin/fiberstat.ha";
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return (false, $"HTTP {(int)response.StatusCode} from {context.Host}");

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var scheme = baseUrl.StartsWith("https") ? "HTTPS" : "HTTP";
            if (html.Contains("Fiber Status", StringComparison.OrdinalIgnoreCase))
                return (true, $"Connected ({scheme}) to AT&T gateway fiber stats page");

            return (false, "Page returned but does not appear to be the AT&T fiber stats page");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return (false, "Connection timed out");
        }
        catch (Exception ex)
        {
            return (false, $"Unexpected error: {ex.Message}");
        }
    }

    private void ParseFiberStat(string html, OntStats stats)
    {
        // Parse DDM values from <h1>MetricName&nbsp;&nbsp;Currently VALUE</h1>
        var ddmMatches = DdmRegex.Matches(html);
        foreach (Match match in ddmMatches)
        {
            var metricName = match.Groups[1].Value.Trim();
            if (!int.TryParse(match.Groups[2].Value, out var rawValue))
                continue;

            if (metricName.Contains("Temperature", StringComparison.OrdinalIgnoreCase))
            {
                stats.TemperatureC = rawValue;
            }
            else if (metricName.Contains("Vcc", StringComparison.OrdinalIgnoreCase))
            {
                stats.VoltageV = rawValue;
            }
            else if (metricName.Contains("Tx Bias", StringComparison.OrdinalIgnoreCase))
            {
                stats.BiasMa = rawValue;
            }
            else if (metricName.Contains("Tx Power", StringComparison.OrdinalIgnoreCase))
            {
                stats.TxPowerDbm = rawValue / 10.0;
            }
            else if (metricName.Contains("Rx Power", StringComparison.OrdinalIgnoreCase))
            {
                stats.RxPowerDbm = rawValue / 10.0;
            }
        }

        // Parse key-value table rows
        var kvMatches = KeyValueRegex.Matches(html);
        foreach (Match match in kvMatches)
        {
            var key = match.Groups[1].Value.Trim();
            var value = match.Groups[2].Value.Trim();

            if (string.IsNullOrEmpty(value))
                continue;

            if (key.Contains("Operational Status", StringComparison.OrdinalIgnoreCase))
                stats.OperationalStatus = value;
            else if (key.Equals("Link State", StringComparison.OrdinalIgnoreCase))
                stats.LinkState = value;
            else if (key.Contains("Vendor Name", StringComparison.OrdinalIgnoreCase))
                stats.VendorName = value;
            else if (key.Contains("Vendor PN", StringComparison.OrdinalIgnoreCase))
                stats.VendorPn = value;
            else if (key.Contains("Vendor SN", StringComparison.OrdinalIgnoreCase))
                stats.VendorSn = value;
            else if (key.Contains("Wave Length", StringComparison.OrdinalIgnoreCase))
                stats.WaveLength = value;
        }
    }

    private void ParseBroadbandStatistics(string html, OntStats stats)
    {
        var kvMatches = KeyValueLooseRegex.Matches(html);
        foreach (Match match in kvMatches)
        {
            var key = match.Groups[1].Value.Trim();
            var value = match.Groups[2].Value.Trim();

            if (string.IsNullOrEmpty(value))
                continue;

            if (key.Equals("Broadband Connection", StringComparison.OrdinalIgnoreCase))
            {
                stats.OperationalStatus ??= value;
            }
            else if (key.Contains("PON Link Status", StringComparison.OrdinalIgnoreCase))
            {
                stats.PonLinkStatus = PonLinkStateExtensions.ParsePonLinkState(value);
            }
            else if (key.Contains("Current Speed", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(value, out var speed))
            {
                stats.BwpSpeedMbps = speed;
            }
        }
    }

    /// <summary>
    /// Derive PON type from the SFP wavelength.
    /// Called after both pages are parsed so wavelength is available.
    /// </summary>
    private static void DerivePonTypeFromWavelength(OntStats stats)
    {
        if (string.IsNullOrWhiteSpace(stats.WaveLength))
            return;

        var cleaned = stats.WaveLength.Replace("nm", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (!int.TryParse(cleaned, out var nm))
            return;

        if (nm is 1490 or 1550)
            stats.PonType = "GPON";
        else if (nm == 1577)
            stats.PonType = "XGS-PON";
    }

    /// <summary>
    /// Tries port-based scheme first, falls back to opposite on connection/SSL failure.
    /// </summary>
    private async Task<string?> ResolveBaseUrlAsync(
        HttpClient client, OntPollContext context, CancellationToken ct)
    {
        var port = context.Port > 0 ? context.Port : 80;
        var primaryScheme = port == 443 ? "https" : "http";
        var fallbackScheme = primaryScheme == "https" ? "http" : "https";

        var primaryUrl = BuildBaseUrl(context.Host, port, primaryScheme);
        try
        {
            using var response = await client.GetAsync($"{primaryUrl}/", ct);
            return primaryUrl;
        }
        catch (HttpRequestException ex) when (
            ex.InnerException is System.Security.Authentication.AuthenticationException
            || ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("{Scheme} failed with SSL error for {Host}, trying {Fallback}",
                primaryScheme.ToUpperInvariant(), context.Host, fallbackScheme.ToUpperInvariant());
        }
        catch (HttpRequestException)
        {
            _logger.LogDebug("{Scheme} connection failed for {Host}, trying {Fallback}",
                primaryScheme.ToUpperInvariant(), context.Host, fallbackScheme.ToUpperInvariant());
        }

        var fallbackUrl = BuildBaseUrl(context.Host, port, fallbackScheme);
        try
        {
            using var response = await client.GetAsync($"{fallbackUrl}/", ct);
            _logger.LogInformation("AT&T gateway {Host} reachable via {Scheme}",
                context.Host, fallbackScheme.ToUpperInvariant());
            return fallbackUrl;
        }
        catch
        {
            return null;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    private static string BuildBaseUrl(string host, int port, string scheme)
    {
        var portSuffix = (scheme == "http" && port == 80) || (scheme == "https" && port == 443)
            ? "" : $":{port}";
        return $"{scheme}://{host}{portSuffix}";
    }

    private async Task<string?> FetchPageAsync(
        HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("HTTP {StatusCode} fetching {Url}", (int)response.StatusCode, url);
                return null;
            }
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch {Url}", url);
            return null;
        }
    }
}
