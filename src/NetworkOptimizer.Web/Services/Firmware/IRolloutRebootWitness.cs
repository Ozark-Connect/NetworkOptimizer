namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>One recorded boot: the firmware the device came up on, and when that boot started.</summary>
public sealed record RolloutBootRecord(string FirmwareVersion, DateTime BootedAt);

/// <summary>
/// Recorded boot evidence for a device, read from the monitoring event store.
/// </summary>
/// <remarks>
/// The console reporting a device Online is the rollout's normal proof that an upgrade landed, and
/// it can arrive after the device is already up. A device that boots onto the target seconds before
/// its offline budget expires would otherwise fail its step and drop its whole model, so this is the
/// second witness the offline path consults before giving up.
/// </remarks>
public interface IRolloutRebootWitness
{
    /// <summary>
    /// The device's earliest recorded boot at or after <paramref name="since"/>, or null when
    /// nothing was recorded.
    /// </summary>
    Task<RolloutBootRecord?> FirstBootSinceAsync(string deviceMac, DateTime since, CancellationToken ct = default);
}
