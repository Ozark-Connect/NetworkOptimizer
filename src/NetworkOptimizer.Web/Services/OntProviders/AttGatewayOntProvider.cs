using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;

namespace NetworkOptimizer.Web.Services.OntProviders;

/// <summary>
/// Scrapes AT&amp;T residential gateways (BGW210, BGW320-500/505) that expose
/// fiber stats via unauthenticated HTTP pages. No login required.
/// </summary>
public class AttGatewayOntProvider : IOntProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AttGatewayOntProvider> _logger;

    private static readonly Regex DdmRegex = new(
        @"<h1>([^<]+?)&nbsp;&nbsp;Currently\s+(-?\d+)</h1>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex KeyValueRegex = new(
        @"<th\s+scope=""row""[^>]*>([^<]+)</th>\s*<td\s+class=""col2""[^>]*>([^<]*)</td>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public AttGatewayOntProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<AttGatewayOntProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string ProviderKey => "att-gateway";
    public string DisplayName => "AT&T Gateway (HTTP)";

    public async Task<OntStats?> PollAsync(OntPollContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = CreateHttpClient(context);
            var stats = new OntStats
            {
                Timestamp = DateTime.UtcNow,
                DeviceHost = context.Host,
                DeviceName = context.Name,
                DeviceModel = "AT&T Gateway"
            };

            // Primary endpoint: DDM optics data
            var fiberStatUrl = BuildUrl(context, "/cgi-bin/fiberstat.ha");
            var fiberHtml = await FetchPageAsync(client, fiberStatUrl, cancellationToken);
            if (fiberHtml == null)
            {
                _logger.LogWarning("Failed to fetch fiberstat.ha from {Host}", context.Host);
                return null;
            }

            ParseFiberStat(fiberHtml, stats);

            // Secondary endpoint: broadband link status (optional)
            var broadbandUrl = BuildUrl(context, "/cgi-bin/broadbandstatistics.ha");
            var broadbandHtml = await FetchPageAsync(client, broadbandUrl, cancellationToken);
            if (broadbandHtml != null)
            {
                ParseBroadbandStatistics(broadbandHtml, stats);
            }

            _logger.LogDebug(
                "AT&T gateway {Host} polled: Rx={RxPower} dBm, Tx={TxPower} dBm, Temp={Temp} C",
                context.Host, stats.RxPowerDbm, stats.TxPowerDbm, stats.TemperatureC);

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
            var client = CreateHttpClient(context);
            var url = BuildUrl(context, "/cgi-bin/fiberstat.ha");

            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (false, $"HTTP {(int)response.StatusCode} from {context.Host}");
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (html.Contains("Fiber Status", StringComparison.OrdinalIgnoreCase))
            {
                return (true, "Connected to AT&T gateway fiber stats page");
            }

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
        var kvMatches = KeyValueRegex.Matches(html);
        foreach (Match match in kvMatches)
        {
            var key = match.Groups[1].Value.Trim();
            var value = match.Groups[2].Value.Trim();

            if (string.IsNullOrEmpty(value))
                continue;

            if (key.Contains("Broadband Connection Source", StringComparison.OrdinalIgnoreCase))
            {
                // If fiber, set PonType generically (page doesn't distinguish GPON vs XGS-PON)
                if (value.Contains("FIBER", StringComparison.OrdinalIgnoreCase))
                    stats.PonType ??= "GPON";
            }
            else if (key.Equals("Broadband Connection", StringComparison.OrdinalIgnoreCase))
            {
                // Fallback operational status from broadband page
                stats.OperationalStatus ??= value;
            }
        }
    }

    private HttpClient CreateHttpClient(OntPollContext context)
    {
        var client = _httpClientFactory.CreateClient("OntProvider");
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }

    private static string BuildUrl(OntPollContext context, string path)
    {
        var port = context.Port > 0 ? context.Port : 80;
        var scheme = port == 443 ? "https" : "http";
        return port == 80 || port == 443
            ? $"{scheme}://{context.Host}{path}"
            : $"{scheme}://{context.Host}:{port}{path}";
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
