using NetworkOptimizer.Monitoring;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;

namespace NetworkOptimizer.Web.Services.CellularModemProviders;

/// <summary>
/// Cellular modem provider for Ubiquiti modems (U-LTE, U5G-Max, U5G Backup, ...).
/// Tries the uiwwand ubus command first (available on all modern UniFi modems),
/// then falls back to raw qmicli commands for older firmware.
/// SSH transport uses the shared UniFiSshService.
/// </summary>
public sealed class QmicliModemProvider : ICellularModemProvider
{
    /// <inheritdoc/>
    public string ProviderKey => "qmicli";

    /// <inheritdoc/>
    public string DisplayName => "Ubiquiti modem (SSH)";

    private readonly ILogger<QmicliModemProvider> _logger;
    private readonly UniFiSshService _sshService;

    // Created per site by ModemMonitorRegistry with that site's device SSH
    // service, so qmicli commands reach the site's modem host (tunnel-routed
    // when the site's devices are reached via agent).
    public QmicliModemProvider(
        ILogger<QmicliModemProvider> logger,
        UniFiSshService sshService)
    {
        _logger = logger;
        _sshService = sshService;
    }

    /// <inheritdoc/>
    public async Task<CellularModemStats?> PollAsync(
        ModemPollContext context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Polling modem {Name} at {Host}", context.Name, context.Host);

        // Try uiwwand first - available on all modern UniFi cellular modems
        var stats = await TryPollViaUiwwandAsync(context);
        if (stats != null)
            return stats;

        // Fall back to raw qmicli commands
        return await PollViaQmicliAsync(context);
    }

    /// <summary>
    /// Poll via UniFi's uiwwand daemon. Returns null if uiwwand is not available
    /// on this device, allowing fallback to qmicli.
    /// </summary>
    private async Task<CellularModemStats?> TryPollViaUiwwandAsync(ModemPollContext context)
    {
        try
        {
            var command = "ubus call uiwwand call '{\"method\":\"get-radio-status\",\"params\":{}}'";
            var (success, output) = await _sshService.RunCommandAsync(context.Host, command);

            if (!success || string.IsNullOrWhiteSpace(output))
            {
                _logger.LogDebug("uiwwand not available on {Name}, falling back to qmicli", context.Name);
                return null;
            }

            // ubus returns "not found" when the service doesn't exist,
            // or a JSON object without "result" when the method is unknown
            if (output.Contains("not found") || !output.Contains("\"result\""))
            {
                _logger.LogDebug("uiwwand not available on {Name}, falling back to qmicli", context.Name);
                return null;
            }

            var stats = UiwwandParser.Parse(output, context.Host, context.Name, context.ModemType);

            if (stats != null && stats.Lte == null && stats.Nr5g == null)
            {
                _logger.LogDebug("uiwwand returned no signal data for {Name}, trying qmicli", context.Name);
                return null;
            }

            if (stats != null)
            {
                _logger.LogInformation(
                    "Successfully polled modem {Name} via uiwwand: {Carrier}, Signal Quality: {Quality}%",
                    context.Name, stats.Carrier, stats.SignalQuality);
            }

            return stats;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "uiwwand poll failed for {Name}, falling back to qmicli", context.Name);
            return null;
        }
    }

    /// <summary>
    /// Poll via raw qmicli commands. Fallback path when uiwwand is unavailable.
    /// </summary>
    private async Task<CellularModemStats?> PollViaQmicliAsync(ModemPollContext context)
    {
        var qmiDevice = string.IsNullOrWhiteSpace(context.TransportPath)
            ? "/dev/wwan0qmi0"
            : context.TransportPath;

        try
        {
            var stats = new CellularModemStats
            {
                ModemHost = context.Host,
                ModemName = context.Name,
                ModemModel = context.ModemType,
                Timestamp = DateTime.UtcNow,
            };

            var combinedCommand =
                $"echo '===SIGNAL===' && qmicli -d {qmiDevice} --device-open-proxy --nas-get-signal-info; " +
                $"echo '===SERVING===' && qmicli -d {qmiDevice} --device-open-proxy --nas-get-serving-system; " +
                $"echo '===CELL===' && qmicli -d {qmiDevice} --device-open-proxy --nas-get-cell-location-info; " +
                $"echo '===BAND===' && qmicli -d {qmiDevice} --device-open-proxy --nas-get-rf-band-info";

            var (success, output) = await _sshService.RunCommandAsync(context.Host, combinedCommand);

            if (!success)
            {
                _logger.LogWarning("Failed to poll modem {Name} via qmicli: {Output}", context.Name, output);
                return null;
            }

            var sections = ParseCombinedOutput(output);

            if (sections.TryGetValue("SIGNAL", out var signalOutput))
            {
                var (lte, nr5g) = QmicliParser.ParseSignalInfo(signalOutput);
                stats.Lte = lte;
                stats.Nr5g = nr5g;
            }

            if (sections.TryGetValue("SERVING", out var servingOutput))
            {
                var (regState, carrier, mcc, mnc, roaming) = QmicliParser.ParseServingSystem(servingOutput);
                stats.RegistrationState = regState;
                stats.Carrier = carrier;
                stats.CarrierMcc = mcc;
                stats.CarrierMnc = mnc;
                stats.IsRoaming = roaming;
            }

            if (sections.TryGetValue("CELL", out var cellOutput))
            {
                var (servingCell, neighbors) = QmicliParser.ParseCellLocationInfo(cellOutput);
                stats.ServingCell = servingCell;
                stats.NeighborCells = neighbors;
            }

            if (sections.TryGetValue("BAND", out var bandOutput))
            {
                stats.ActiveBand = QmicliParser.ParseRfBandInfo(bandOutput);
            }

            _logger.LogInformation(
                "Successfully polled modem {Name} via qmicli: {Carrier}, Signal Quality: {Quality}%",
                context.Name, stats.Carrier, stats.SignalQuality);

            return stats;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling modem {Name}", context.Name);
            return null;
        }
    }

    /// <inheritdoc/>
    public Task<(bool success, string message)> TestConnectionAsync(
        ModemPollContext context,
        CancellationToken cancellationToken = default)
    {
        return _sshService.TestConnectionAsync(context.Host);
    }

    /// <summary>
    /// Split combined SSH output into sections by marker.
    /// </summary>
    private static Dictionary<string, string> ParseCombinedOutput(string output)
    {
        var sections = new Dictionary<string, string>();
        var markers = new[] { "===SIGNAL===", "===SERVING===", "===CELL===", "===BAND===" };
        var keys = new[] { "SIGNAL", "SERVING", "CELL", "BAND" };

        for (int i = 0; i < markers.Length; i++)
        {
            var startIndex = output.IndexOf(markers[i]);
            if (startIndex == -1) continue;

            startIndex += markers[i].Length;

            var endIndex = output.Length;
            for (int j = i + 1; j < markers.Length; j++)
            {
                var nextMarker = output.IndexOf(markers[j], startIndex);
                if (nextMarker != -1)
                {
                    endIndex = nextMarker;
                    break;
                }
            }

            sections[keys[i]] = output.Substring(startIndex, endIndex - startIndex).Trim();
        }

        return sections;
    }
}
