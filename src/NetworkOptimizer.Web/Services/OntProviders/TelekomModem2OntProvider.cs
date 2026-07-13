using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;

namespace NetworkOptimizer.Web.Services.OntProviders;

/// <summary>
/// ONT provider for the Deutsche Telekom "Glasfaser-Modem 2" GPON ONT, used in
/// modem/bridge mode ahead of a third-party router on Telekom FTTH connections.
/// Unlike every other provider here, no login/session is needed: the device
/// serves DDM/link stats openly over plain HTTP at /ONT/client/data/Status.json,
/// which the same page the LAN-connected web UI polls for its Status view.
/// </summary>
public sealed class TelekomModem2OntProvider : IOntProvider
{
    public string ProviderKey => "telekom-modem2";
    public string DisplayName => "Telekom Glasfaser-Modem 2 (HTTP)";

    private const int TimeoutSeconds = 10;
    private const string StatusPath = "/ONT/client/data/Status.json";

    private readonly ILogger<TelekomModem2OntProvider> _logger;

    public TelekomModem2OntProvider(ILogger<TelekomModem2OntProvider> logger)
    {
        _logger = logger;
    }

    public async Task<OntStats?> PollAsync(OntPollContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
        {
            _logger.LogWarning("Telekom Modem 2 ONT poll requested but Host is empty (config {Id})", context.Id);
            return null;
        }

        try
        {
            using var client = CreateClient();
            var json = await client.GetStringAsync($"{BuildBaseUrl(context)}{StatusPath}", cancellationToken);

            var stats = new OntStats
            {
                Timestamp = DateTime.UtcNow,
                DeviceHost = context.Host,
                DeviceName = context.Name,
                DeviceModel = "Glasfaser-Modem 2",
            };
            ApplyStatus(json, stats);

            _logger.LogDebug(
                "Telekom Modem 2 ONT {Name} polled: Rx={Rx} dBm, Tx={Tx} dBm, Link={Link}",
                context.Name, stats.RxPowerDbm?.ToString("F2") ?? "-",
                stats.TxPowerDbm?.ToString("F2") ?? "-", stats.LinkState ?? "-");

            return stats;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error polling Telekom Modem 2 ONT {Name} at {Host}", context.Name, context.ConfiguredHost ?? context.Host);
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
            using var client = CreateClient();
            var json = await client.GetStringAsync($"{BuildBaseUrl(context)}{StatusPath}", cancellationToken);

            var stats = new OntStats();
            ApplyStatus(json, stats);

            if (stats.RxPowerDbm is null && stats.TxPowerDbm is null)
                return (false, "Connected but response does not contain expected PON status fields");

            return (true,
                $"Connected (HTTP) - RX: {stats.RxPowerDbm?.ToString("F2") ?? "?"} dBm, TX: {stats.TxPowerDbm?.ToString("F2") ?? "?"} dBm");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses the flat array of {"vartype","varid","varvalue"} entries returned by
    /// Status.json. Defensive throughout: any missing or malformed field is simply
    /// left at its OntStats default rather than throwing.
    /// </summary>
    internal static void ApplyStatus(string json, OntStats stats)
    {
        var values = ParseVarArray(json);

        if (GetValue(values, "device_name") is { Length: > 0 } deviceName)
            stats.DeviceModel = deviceName;

        if (GetValue(values, "hardware_revision") is { Length: > 0 } hwRevision)
            stats.VendorPn = hwRevision;

        if (GetValue(values, "serial_number") is { Length: > 0 } serial)
            stats.VendorSn = serial;

        stats.TxPowerDbm = ParseDouble(GetValue(values, "txpower")) ?? stats.TxPowerDbm;
        stats.RxPowerDbm = ParseDouble(GetValue(values, "rxpower")) ?? stats.RxPowerDbm;
        stats.BipErrors = ParseLong(GetValue(values, "rxbip_crc")) ?? stats.BipErrors;
        stats.LinkUptimeSeconds = ParseLong(GetValue(values, "stability")) ?? stats.LinkUptimeSeconds;

        // hardware_state's own semantics (from the device's status.js): "0" is a hardware
        // fault, any other value (including absent/unknown) reads as Ok. ploam_success
        // confirms the ONU finished GPON activation; together they gate whether we trust
        // "Operation" or need to fall back to whatever transitional ploam_state reports.
        var hardwareOk = GetValue(values, "hardware_state") != "0";
        var ploamSuccess = GetValue(values, "ploam_success") == "1";
        var up = hardwareOk && ploamSuccess;

        stats.PonLinkStatus = up
            ? PonLinkState.Operation
            : PonLinkStateExtensions.ParsePonLinkState(GetValue(values, "ploam_state"));
        stats.OperationalStatus = up ? "Up" : "Down";
        stats.LinkState = stats.PonLinkStatus.ToDisplayString();
    }

    private static Dictionary<string, string> ParseVarArray(string json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("vartype", out var typeEl) || typeEl.GetString() != "value")
                    continue;
                if (!entry.TryGetProperty("varid", out var idEl) || !entry.TryGetProperty("varvalue", out var valueEl))
                    continue;

                var id = idEl.GetString();
                var value = valueEl.GetString();
                if (!string.IsNullOrEmpty(id) && value != null)
                    result[id] = value;
            }
        }
        catch (JsonException) { }

        return result;
    }

    private static string? GetValue(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var v) ? v : null;

    private static double? ParseDouble(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var val) ? val : null;

    private static long? ParseLong(string? text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var val) ? val : null;

    /// <summary>
    /// The device's firmware rejects requests with no Accept-Language header at all as an
    /// "Invalid Request" 400 (confirmed live: identical requests succeed once this header is
    /// present, regardless of network path). Matches Netzwerkfehler/hass-GFM2, a working Home
    /// Assistant integration for this same device, which sets exactly this header and nothing
    /// else beyond a plain GET.
    /// </summary>
    internal static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
        client.DefaultRequestHeaders.Add("Accept-Language", "en");
        return client;
    }

    private static string BuildBaseUrl(OntPollContext context)
    {
        var port = context.Port > 0 ? context.Port : 80;
        var portSuffix = port == 80 ? "" : $":{port}";
        return $"http://{context.Host}{portSuffix}";
    }
}
