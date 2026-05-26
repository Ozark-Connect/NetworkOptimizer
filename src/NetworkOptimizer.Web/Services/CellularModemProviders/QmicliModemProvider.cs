using NetworkOptimizer.Monitoring;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services;

namespace NetworkOptimizer.Web.Services.CellularModemProviders;

/// <summary>
/// Cellular modem provider for Ubiquiti modems (U-LTE, U5G-Max, ...).
/// Speaks SSH to the modem and runs qmicli commands, then parses the output
/// with the existing static QmicliParser.
/// </summary>
/// <remarks>
/// Behavior moved verbatim from CellularModemService.ExecutePollAsync.
/// No protocol changes - same combined-command pattern, same section markers,
/// same parser calls. The SSH transport remains UniFiSshService.
/// </remarks>
public sealed class QmicliModemProvider : ICellularModemProvider
{
    /// <inheritdoc/>
    public string ProviderKey => "qmicli";

    /// <inheritdoc/>
    public string DisplayName => "Ubiquiti modem (qmicli)";

    private readonly ILogger<QmicliModemProvider> _logger;
    private readonly UniFiSshService _sshService;

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
        var qmiDevice = string.IsNullOrWhiteSpace(context.TransportPath)
            ? "/dev/wwan0qmi0"
            : context.TransportPath;

        _logger.LogInformation("Polling modem {Name} at {Host} via qmicli",
            context.Name, context.Host);

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
                _logger.LogWarning("Failed to poll modem {Name}: {Output}", context.Name, output);
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
                "Successfully polled modem {Name}: {Carrier}, Signal Quality: {Quality}%",
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
        // Delegates to the existing UniFiSshService connection test.
        // No protocol-level credential check beyond SSH reachability.
        return _sshService.TestConnectionAsync(context.Host);
    }

    /// <summary>
    /// Split combined SSH output into sections by marker. Pulled verbatim
    /// from the prior in-service helper so existing tests stay green.
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
