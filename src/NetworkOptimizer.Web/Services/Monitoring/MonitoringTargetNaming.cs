using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>Who names a monitoring target.</summary>
public static class MonitoringTargetNaming
{
    /// <summary>
    /// A target seeded from a UniFi device, named as the UniFi Console names the device. The
    /// collection agent keeps that name in step with the console on every reconcile, so it is not
    /// editable in Network Optimizer. A retired target keeps the last name it had.
    /// </summary>
    public static bool IsConsoleNamed(MonitoringTarget t) =>
        t.AutoDiscovered && t.TargetType == MonitoringTargetType.Fabric && !string.IsNullOrEmpty(t.DeviceMac);
}
