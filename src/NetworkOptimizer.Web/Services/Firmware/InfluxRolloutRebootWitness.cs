using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <inheritdoc cref="IRolloutRebootWitness" />
public sealed class InfluxRolloutRebootWitness : IRolloutRebootWitness
{
    private readonly MonitoringInfluxClient _influx;
    private readonly ILogger<InfluxRolloutRebootWitness> _logger;

    public InfluxRolloutRebootWitness(MonitoringInfluxClient influx, ILogger<InfluxRolloutRebootWitness> logger)
    {
        _influx = influx;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RolloutBootRecord?> FirstBootSinceAsync(string deviceMac, DateTime since, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceMac)) return null;

        try
        {
            // Reboot records are tagged with the MAC stripped of separators, while the rollout carries
            // the colon form. Compare both through one canonical shape rather than either raw string.
            var wanted = UniFiDeviceUpgradeCommand.NormalizeMac(deviceMac);
            var reboots = await _influx.QueryDeviceRebootsInRangeAsync(since, DateTime.UtcNow, ct);

            return reboots
                .Where(r => !string.IsNullOrWhiteSpace(r.DeviceMac)
                    && UniFiDeviceUpgradeCommand.NormalizeMac(r.DeviceMac) == wanted
                    && r.BootedAt >= since
                    && !string.IsNullOrWhiteSpace(r.FirmwareVersion))
                .OrderBy(r => r.BootedAt)
                .Select(r => new RolloutBootRecord(r.FirmwareVersion!, r.BootedAt))
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            // No answer leaves the offline path exactly as it was: the step still fails on its budget.
            _logger.LogDebug(ex, "Reboot witness lookup failed for {Mac}", deviceMac);
            return null;
        }
    }
}
