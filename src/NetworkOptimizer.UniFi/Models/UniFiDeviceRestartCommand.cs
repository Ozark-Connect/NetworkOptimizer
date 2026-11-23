using NetworkOptimizer.Core;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// Request bodies for the cmd/devmgr restart and PoE power-cycle commands, as UniFi Network's own
/// device actions send them (web UI 11.1.47).
/// </summary>
[VendorSpecific("UniFi", "cmd/devmgr restart / power-cycle bodies")]
public static class UniFiDeviceRestartCommand
{
    /// <summary>Restart a device.</summary>
    public const string Restart = "restart";

    /// <summary>Cut and restore PoE on one switch port.</summary>
    public const string PowerCycle = "power-cycle";

    /// <summary>The restart kind UniFi Network's Restart action sends.</summary>
    public const string SoftReboot = "soft";

    /// <summary>POST cmd/devmgr body that restarts a device.</summary>
    public static Dictionary<string, object?> BuildRestartBody(string mac)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mac);

        return new Dictionary<string, object?>
        {
            ["mac"] = UniFiDeviceUpgradeCommand.NormalizeMac(mac),
            ["reboot_type"] = SoftReboot,
            ["cmd"] = Restart
        };
    }

    /// <summary>POST cmd/devmgr body that power-cycles one PoE port on a switch.</summary>
    /// <param name="switchMac">The switch that powers the port.</param>
    /// <param name="portIdx">The port's <c>port_idx</c>.</param>
    public static Dictionary<string, object?> BuildPowerCycleBody(string switchMac, int portIdx)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(switchMac);
        ArgumentOutOfRangeException.ThrowIfLessThan(portIdx, 1);

        return new Dictionary<string, object?>
        {
            ["mac"] = UniFiDeviceUpgradeCommand.NormalizeMac(switchMac),
            ["port_idx"] = portIdx,
            ["cmd"] = PowerCycle
        };
    }
}
