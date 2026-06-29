using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;

namespace NetworkOptimizer.Web.Services.OntProviders;

/// <summary>
/// ONT provider for Lantiq/MaxLinear-based GPON/XGS-PON SFP sticks running
/// 8311 community firmware (LuCI web UI). Covers WAS-110, PRX126, Nokia G-010S-P,
/// and other sticks flashed with 8311 firmware by djGrrr.
///
/// Uses the JSON endpoint at /cgi-bin/luci/admin/8311/gpon_status which returns
/// pre-formatted DDM data. Auth via LuCI session cookie (sysauth).
/// Always HTTPS with self-signed certs.
/// </summary>
public sealed class Lantiq8311OntProvider : IOntProvider
{
    public string ProviderKey => "8311";
    public string DisplayName => "8311 Community Firmware (HTTP)";

    private const int TimeoutSeconds = 10;
    private const string GponStatusPath = "/cgi-bin/luci/admin/8311/gpon_status";
    private const string LoginPath = "/cgi-bin/luci";
    // Raw `pontop -g "FEC Status & Counters" -b` text (text/plain). The DDM gpon_status JSON does not
    // carry error counters; FEC/BIP only live on this pontop page.
    private const string FecCountersPath = "/cgi-bin/luci/admin/8311/pontop/fec";

    // KV row in the pontop batch output: "Label<padding> : Value". Only numeric-valued rows match
    // (so "FEC upstream : ON" and the "OPTION  VALUE" header are skipped).
    private static readonly Regex FecCounterRegex = new(
        @"^(.*\S)\s+:\s+(\d+)\s*$", RegexOptions.Compiled);

    private readonly ILogger<Lantiq8311OntProvider> _logger;

    public Lantiq8311OntProvider(ILogger<Lantiq8311OntProvider> logger)
    {
        _logger = logger;
    }

    public async Task<OntStats?> PollAsync(OntPollContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
        {
            _logger.LogWarning("8311 ONT poll requested but Host is empty (config {Id})", context.Id);
            return null;
        }

        try
        {
            var baseUrl = BuildBaseUrl(context);
            using var handler = CreateHandler();
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };

            if (!await LoginAsync(client, baseUrl, context, cancellationToken))
            {
                _logger.LogWarning("8311 ONT {Name}: login failed", context.Name);
                return null;
            }

            var json = await client.GetStringAsync($"{baseUrl}{GponStatusPath}", cancellationToken);
            var stats = ParseGponStatus(json, context);

            // Best-effort FEC/BIP counters from the pontop page. Fully isolated: any failure here must
            // not affect the DDM stats already parsed above, so a missing page or parse error just
            // leaves the error counters null.
            await TryAddFecCountersAsync(client, baseUrl, stats, cancellationToken);

            _logger.LogDebug("8311 ONT {Name} polled: Rx={Rx} dBm, Tx={Tx} dBm, Temp={Temp} C, Mode={Mode}",
                context.Name, stats.RxPowerDbm?.ToString("F2") ?? "-",
                stats.TxPowerDbm?.ToString("F2") ?? "-",
                stats.TemperatureC?.ToString("F1") ?? "-",
                stats.PonType ?? "-");

            return stats;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error polling 8311 ONT {Name} at {Host}", context.Name, context.Host);
            return null;
        }
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(
        OntPollContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
            return (false, "Host is empty");

        try
        {
            var baseUrl = BuildBaseUrl(context);
            using var handler = CreateHandler();
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };

            if (!await LoginAsync(client, baseUrl, context, cancellationToken))
                return (false, "Login failed - check username/password (default: root / no password)");

            using var response = await client.GetAsync($"{baseUrl}{GponStatusPath}", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return (false, $"GPON status page returned HTTP {(int)response.StatusCode}");

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!json.Contains("pon_mode", StringComparison.OrdinalIgnoreCase))
                return (false, "Connected but response does not contain expected GPON status fields");

            var stats = ParseGponStatus(json, context);
            return (true, $"Connected (HTTPS) - {stats.PonType ?? "PON"} mode, RX: {stats.RxPowerDbm?.ToString("F2") ?? "?"} dBm");
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"SSL connection failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// LuCI form login: POST username/password to /cgi-bin/luci, get sysauth cookie back.
    /// Many 8311 sticks ship with no root password set - empty password is valid.
    /// Uses CookieContainer with AllowAutoRedirect so the sysauth cookie is automatically
    /// stored and sent on subsequent requests.
    /// </summary>
    private async Task<bool> LoginAsync(
        HttpClient client, string baseUrl, OntPollContext context, CancellationToken ct)
    {
        var username = context.Username ?? "root";
        var password = context.Password ?? "";

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["luci_username"] = username,
            ["luci_password"] = password,
        });

        using var response = await client.PostAsync($"{baseUrl}{LoginPath}", content, ct);

        // After login (with auto-redirect following), verify we can access a protected page.
        // The CookieContainer handles the sysauth cookie automatically.
        using var probe = await client.GetAsync($"{baseUrl}{GponStatusPath}", ct);
        if (probe.IsSuccessStatusCode)
        {
            var probeContent = await probe.Content.ReadAsStringAsync(ct);
            return probeContent.Contains("pon_mode", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Parses the JSON response from /cgi-bin/luci/admin/8311/gpon_status.
    /// Format: {"power":"-21.19 dBm / 5.36 dBm / 14.18 mA", "temperature":"57.77 °C (...) / 55.24 °C (...) / 46.50 °C (...)", ...}
    /// </summary>
    private OntStats ParseGponStatus(string json, OntPollContext context)
    {
        var stats = new OntStats
        {
            Timestamp = DateTime.UtcNow,
            DeviceHost = context.Host,
            DeviceName = context.Name,
            DeviceModel = "8311 ONT",
        };

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // power: "-21.19 dBm / 5.36 dBm / 14.18 mA"
            if (root.TryGetProperty("power", out var powerEl))
            {
                var power = powerEl.GetString();
                if (power != null)
                {
                    var parts = power.Split('/');
                    if (parts.Length >= 3)
                    {
                        stats.RxPowerDbm = ParseNumericValue(parts[0]);
                        stats.TxPowerDbm = ParseNumericValue(parts[1]);
                        stats.BiasMa = ParseNumericValue(parts[2]);
                    }
                }
            }

            // temperature: "57.77 °C (136.0 °F) / 55.24 °C (131.4 °F) / 46.50 °C (115.7 °F)"
            // Third value is optic temperature
            if (root.TryGetProperty("temperature", out var tempEl))
            {
                var temp = tempEl.GetString();
                if (temp != null)
                {
                    var parts = temp.Split('/');
                    if (parts.Length >= 3)
                    {
                        stats.TemperatureC = ParseNumericValue(parts[2]);
                    }
                    else if (parts.Length >= 1)
                    {
                        stats.TemperatureC = ParseNumericValue(parts[0]);
                    }
                }
            }

            // voltage: "3.31 V"
            if (root.TryGetProperty("voltage", out var voltEl))
            {
                stats.VoltageV = ParseNumericValue(voltEl.GetString());
            }

            // pon_mode: "XGS-PON" or "GPON"
            if (root.TryGetProperty("pon_mode", out var modeEl))
            {
                stats.PonType = modeEl.GetString();
            }

            // status: "O5.1, Associated state"
            if (root.TryGetProperty("status", out var statusEl))
            {
                stats.LinkState = statusEl.GetString();
            }

            // module_info: "AZORES WAS-110 V1.0 (bfw)"
            if (root.TryGetProperty("module_info", out var moduleEl))
            {
                var moduleInfo = moduleEl.GetString();
                if (!string.IsNullOrEmpty(moduleInfo))
                    stats.DeviceModel = moduleInfo;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to parse 8311 gpon_status JSON from {Host}", context.Host);
        }

        return stats;
    }

    /// <summary>
    /// Best-effort fetch + parse of the pontop "FEC Status &amp; Counters" page into the error counters.
    /// Isolated in its own try/catch so a missing page, auth quirk, or format change can never disturb
    /// the DDM stats; on any failure FecErrors/BipErrors simply stay null.
    /// </summary>
    private async Task TryAddFecCountersAsync(HttpClient client, string baseUrl, OntStats stats, CancellationToken ct)
    {
        try
        {
            var text = await client.GetStringAsync($"{baseUrl}{FecCountersPath}", ct);
            ParseFecCounters(text, stats);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "8311 ONT {Name}: FEC/BIP counters unavailable", stats.DeviceName);
        }
    }

    /// <summary>
    /// Parses the raw pontop FEC page (KV rows "Label : Value"). Maps "Uncorrected FEC codewords" -&gt;
    /// FecErrors and "BIP errors" -&gt; BipErrors - both read 0 on a healthy link, so they are clean
    /// data-loss signals. "Corrected FEC codewords" is deliberately NOT used: FEC correcting errors is
    /// benign (the optic is still delivering), so counting it would penalize a working link.
    /// </summary>
    internal static void ParseFecCounters(string text, OntStats stats)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var match = FecCounterRegex.Match(rawLine.TrimEnd('\r'));
            if (!match.Success || !long.TryParse(match.Groups[2].Value, out var value))
                continue;

            var key = match.Groups[1].Value.Trim();
            if (key.Equals("Uncorrected FEC codewords", StringComparison.OrdinalIgnoreCase))
                stats.FecErrors = value;
            else if (key.Equals("BIP errors", StringComparison.OrdinalIgnoreCase))
                stats.BipErrors = value;
        }
    }

    private static double? ParseNumericValue(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Regex.Match(text, @"(-?[\d.]+)");
        if (match.Success && double.TryParse(match.Groups[1].Value,
            NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
            return val;
        return null;
    }

    private static string BuildBaseUrl(OntPollContext context)
    {
        var port = context.Port > 0 ? context.Port : 443;
        var scheme = port == 80 ? "http" : "https";
        var portSuffix = (scheme == "https" && port == 443) || (scheme == "http" && port == 80)
            ? "" : $":{port}";
        return $"{scheme}://{context.Host}{portSuffix}";
    }

    private static HttpClientHandler CreateHandler()
    {
        return new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            CookieContainer = new CookieContainer(),
            UseCookies = true,
        };
    }
}
