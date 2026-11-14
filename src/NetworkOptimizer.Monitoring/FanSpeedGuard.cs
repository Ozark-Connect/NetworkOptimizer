using System.Collections.Concurrent;

namespace NetworkOptimizer.Monitoring;

/// <summary>
/// Holds back a fan speed of 0 rpm that follows a spinning fan until a second reading agrees.
/// A device's SNMP agent answers 0 for a moment while it restarts (a gateway provisioning), and
/// one such reading stored is a fan that stopped and never did. A fan that has never been seen
/// spinning reports 0 as its normal reading, so that passes through.
/// </summary>
public sealed class FanSpeedGuard
{
    private readonly ConcurrentDictionary<string, bool> _spinning = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _zeroPending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The fan speed to store for this reading: the reading itself, or null when it is a first 0
    /// after a spinning fan and has to be confirmed by the next one.
    /// </summary>
    /// <param name="deviceKey">The device, unique within the caller (a MAC, or site and MAC).</param>
    /// <param name="rpm">The reading, or null when the device reported none.</param>
    public int? Accept(string deviceKey, int? rpm)
    {
        if (rpm is null) return null;
        if (rpm > 0)
        {
            _spinning[deviceKey] = true;
            _zeroPending.TryRemove(deviceKey, out _);
            return rpm;
        }

        if (!_spinning.ContainsKey(deviceKey)) return rpm;
        if (_zeroPending.TryAdd(deviceKey, true)) return null;

        // Two in a row: the fan did stop.
        _spinning.TryRemove(deviceKey, out _);
        _zeroPending.TryRemove(deviceKey, out _);
        return rpm;
    }
}
