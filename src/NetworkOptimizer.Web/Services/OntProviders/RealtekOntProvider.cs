using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;

namespace NetworkOptimizer.Web.Services.OntProviders;

/// <summary>
/// ONT provider for Realtek-based GPON stick modules (DFP-34X-2C2, etc.)
/// that expose a web UI with form-based login and status_pon.asp for DDM data.
/// </summary>
public sealed class RealtekOntProvider : IOntProvider
{
    public string ProviderKey => "realtek-ont";
    public string DisplayName => "Realtek ONT Stick (HTTP)";

    private const int TimeoutSeconds = 10;

    private readonly ILogger<RealtekOntProvider> _logger;

    public RealtekOntProvider(ILogger<RealtekOntProvider> logger)
    {
        _logger = logger;
    }

    public async Task<OntStats?> PollAsync(OntPollContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
        {
            _logger.LogWarning("Realtek ONT poll requested but Host is empty (config {Id})", context.Id);
            return null;
        }

        try
        {
            var baseUrl = BuildBaseUrl(context);
            using var handler = new HttpClientHandler
            {
                CookieContainer = new CookieContainer(),
                UseCookies = true,
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };

            if (!await LoginAsync(client, baseUrl, context, cancellationToken))
            {
                _logger.LogWarning("Realtek ONT {Name}: login failed", context.Name);
                return null;
            }

            var ponHtml = await client.GetStringAsync($"{baseUrl}/status_pon.asp", cancellationToken);
            var stats = ParseStatusPon(ponHtml, context);

            try { await client.GetAsync($"{baseUrl}/admin/logout.asp", cancellationToken); }
            catch { }

            _logger.LogDebug("Realtek ONT {Name} polled: Rx={Rx} dBm, Tx={Tx} dBm, Temp={Temp} C",
                context.Name, stats.RxPowerDbm?.ToString("F1") ?? "-",
                stats.TxPowerDbm?.ToString("F1") ?? "-",
                stats.TemperatureC?.ToString("F0") ?? "-");

            return stats;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error polling Realtek ONT {Name} at {Host}", context.Name, context.Host);
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
            using var handler = new HttpClientHandler
            {
                CookieContainer = new CookieContainer(),
                UseCookies = true,
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };

            if (!await LoginAsync(client, baseUrl, context, cancellationToken))
                return (false, "Login failed - check username/password");

            var ponHtml = await client.GetStringAsync($"{baseUrl}/status_pon.asp", cancellationToken);
            if (!ponHtml.Contains("PON Status", StringComparison.OrdinalIgnoreCase))
                return (false, "Connected but PON Status page not found");

            try { await client.GetAsync($"{baseUrl}/admin/logout.asp", cancellationToken); }
            catch { }

            return (true, "Connected - PON Status page accessible");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    private static string BuildBaseUrl(OntPollContext context)
    {
        var port = context.Port > 0 ? context.Port : 80;
        var portSuffix = port == 80 ? "" : $":{port}";
        return $"http://{context.Host}{portSuffix}";
    }

    /// <summary>
    /// Form-based login: POST username and plain-text password to /boaform/admin/formLogin.
    /// </summary>
    private async Task<bool> LoginAsync(
        HttpClient client, string baseUrl, OntPollContext context, CancellationToken ct)
    {
        var username = context.Username ?? "admin";
        var password = context.Password ?? "admin";

        var loginUrl = $"{baseUrl}/boaform/admin/formLogin";
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username,
            ["psd"] = password,
            ["submit-url"] = "/admin/login.asp",
        });

        var response = await client.PostAsync(loginUrl, content, ct);

        if (response.StatusCode == HttpStatusCode.Redirect ||
            response.StatusCode == HttpStatusCode.MovedPermanently ||
            response.StatusCode == HttpStatusCode.OK)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (body.Contains("login.asp", StringComparison.OrdinalIgnoreCase) &&
                body.Contains("error", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        return false;
    }

    private OntStats ParseStatusPon(string html, OntPollContext context)
    {
        var stats = new OntStats
        {
            Timestamp = DateTime.UtcNow,
            DeviceHost = context.Host,
            DeviceName = context.Name,
            DeviceModel = "Realtek ONT",
        };

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var rows = doc.DocumentNode.SelectNodes("//tr[@bgcolor='#DDDDDD']");
        if (rows == null) return stats;

        foreach (var row in rows)
        {
            var cells = row.SelectNodes("td");
            if (cells == null || cells.Count < 2) continue;

            var label = cells[0].InnerText.Trim();
            var value = cells[1].InnerText.Trim();

            if (label.Contains("Temperature", StringComparison.OrdinalIgnoreCase))
            {
                stats.TemperatureC = ParseDouble(value);
            }
            else if (label.Contains("Voltage", StringComparison.OrdinalIgnoreCase))
            {
                stats.VoltageV = ParseDouble(value);
            }
            else if (label.Contains("Tx Power", StringComparison.OrdinalIgnoreCase))
            {
                stats.TxPowerDbm = ParseDouble(value);
            }
            else if (label.Contains("Rx Power", StringComparison.OrdinalIgnoreCase))
            {
                stats.RxPowerDbm = ParseDouble(value);
            }
            else if (label.Contains("Bias Current", StringComparison.OrdinalIgnoreCase))
            {
                stats.BiasMa = ParseDouble(value);
            }
            else if (label.Contains("ONU State", StringComparison.OrdinalIgnoreCase))
            {
                stats.LinkState = value;
            }
        }

        stats.PonType = "GPON";

        return stats;
    }

    private static double? ParseDouble(string text)
    {
        var match = Regex.Match(text, @"(-?[\d.]+)");
        if (match.Success && double.TryParse(match.Groups[1].Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var val))
            return val;
        return null;
    }
}
