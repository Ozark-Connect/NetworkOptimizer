using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.Monitoring.HealthChecks;

/// <summary>
/// The device a check runs on: from UniFi Network's device list when it answers, from the check's
/// persisted last-known fields when it does not.
/// </summary>
public sealed record HealthCheckTarget(
    string Name,
    string? Host,
    DeviceType Type,
    DeviceType HardwareType,
    bool FromLastKnown)
{
    /// <summary>
    /// Gateway hardware is a gateway for SSH credentials and remedies whatever role it is in: a
    /// UDR meshing as an access point still runs UniFi OS and still takes the console credentials.
    /// </summary>
    public DeviceType EffectiveType => HardwareType == DeviceType.Gateway ? DeviceType.Gateway : Type;

    /// <summary>
    /// The target as UniFi Network lists it. A listing without an address keeps the last-known one.
    /// </summary>
    public static HealthCheckTarget FromDevice(DiscoveredDevice device, HealthCheckDefinition check) => new(
        device.Name,
        string.IsNullOrWhiteSpace(device.DisplayIpAddress) ? check.LastKnownHost : device.DisplayIpAddress,
        device.Type,
        device.HardwareType,
        FromLastKnown: false);

    /// <summary>The target from the check's last-known fields, or null when none were ever recorded.</summary>
    public static HealthCheckTarget? FromCheck(HealthCheckDefinition check)
    {
        if (check.LastKnownDeviceType is not { } type) return null;
        return new HealthCheckTarget(
            string.IsNullOrWhiteSpace(check.LastKnownDeviceName) ? check.DeviceMac : check.LastKnownDeviceName,
            check.LastKnownHost,
            type,
            check.LastKnownHardwareType ?? type,
            FromLastKnown: true);
    }

    /// <summary>
    /// Copies what UniFi Network lists for the device onto the check's last-known fields. An empty
    /// address never overwrites a known one. Returns whether anything changed.
    /// </summary>
    public static bool Remember(HealthCheckDefinition check, DiscoveredDevice device, DateTime now)
    {
        var host = string.IsNullOrWhiteSpace(device.DisplayIpAddress) ? check.LastKnownHost : device.DisplayIpAddress;
        if (check.LastKnownHost == host
            && check.LastKnownDeviceName == device.Name
            && check.LastKnownDeviceType == device.Type
            && check.LastKnownHardwareType == device.HardwareType)
            return false;

        check.LastKnownHost = host;
        check.LastKnownDeviceName = device.Name;
        check.LastKnownDeviceType = device.Type;
        check.LastKnownHardwareType = device.HardwareType;
        check.LastKnownAt = now;
        return true;
    }
}
