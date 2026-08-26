using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Suppresses duplicate device offline/recovered notifications when both the monitoring target
/// evaluator (ICMP probes on Fabric targets) and the device state evaluator (UniFi API state)
/// fire for the same device within a short window. Whichever fires first wins; the second is
/// suppressed.
/// </summary>
public class DeviceOfflineDeduplicator
{
    /// <summary>
    /// Two alerts for the same device within this window are the same physical event seen by
    /// two detection paths. Wide enough to cover any poll-interval lag between them, tight
    /// enough that a genuinely separate event (device recovers then goes down again) is not
    /// suppressed.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, DateTime> _lastOffline = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _lastRecovered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Try to claim the right to publish an offline or recovered alert for a device. Returns
    /// true when this is the first alert for this device within the window (publish it), false
    /// when another source already fired for the same device (suppress it).
    /// </summary>
    /// <param name="deviceMac">Device MAC, the stable cross-source identifier.</param>
    /// <param name="isRecovery">True for a recovered alert, false for an offline alert.</param>
    /// <param name="now">Current time, injectable for tests.</param>
    public bool TryClaimSlot(string? deviceMac, bool isRecovery, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(deviceMac))
            return true;

        var dict = isRecovery ? _lastRecovered : _lastOffline;
        var key = NormalizeMac(deviceMac);

        while (true)
        {
            if (dict.TryGetValue(key, out var existing))
            {
                if ((now - existing).Duration() < Window)
                    return false;

                if (dict.TryUpdate(key, now, existing))
                    return true;
            }
            else
            {
                if (dict.TryAdd(key, now))
                    return true;
            }
        }
    }

    private static string NormalizeMac(string mac) =>
        mac.Replace(":", "").Replace("-", "").ToLowerInvariant();
}
