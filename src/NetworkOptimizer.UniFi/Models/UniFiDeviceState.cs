using NetworkOptimizer.Core;
using NetworkOptimizer.Core.Models;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// The numeric values of the UniFi device <c>state</c> field, as defined by UniFi Network. Only a
/// subset are "offline"; transient states (provisioning, updating, adopting) and update-available
/// are NOT offline, which the legacy <c>state == 1</c> checks got wrong. Values mirror UniFi
/// Network's own status enum.
/// </summary>
public enum UniFiDeviceState
{
    Disconnected = 0,
    Connected = 1,
    Pending = 2,
    FirmwareMismatch = 3,
    Upgrading = 4,
    Provisioning = 5,
    HeartbeatMissed = 6,
    Adopting = 7,
    Deleting = 8,
    InformError = 9,
    AdoptionFailed = 10,
    Isolated = 11,
    IncorrectTopology = 13
}

/// <summary>
/// Maps the raw UniFi device <c>state</c> int to a vendor-neutral <see cref="DeviceStatus"/>
/// (bucket + display label), mirroring the labels UniFi Network shows in its own UI.
/// </summary>
[VendorSpecific("UniFi", "state->status mapping; labels match the UniFi Console device states.")]
public static class UniFiDeviceStateMap
{
    /// <summary>Resolve a raw UniFi <c>state</c> value to its display status.</summary>
    public static DeviceStatus ToStatus(int state) => (UniFiDeviceState)state switch
    {
        UniFiDeviceState.Connected => new(DeviceStatusKind.Online, "Online"),
        // In practice a device with a pending firmware update reports state=1 and signals the
        // update via the separate `upgradable` flag, so this branch is effectively never hit
        // (verified on live consoles). Kept for completeness, and it's correct if it ever appears.
        UniFiDeviceState.FirmwareMismatch => new(DeviceStatusKind.Online, "Update Available"),

        UniFiDeviceState.Pending => new(DeviceStatusKind.Transitional, "Pending"),
        UniFiDeviceState.Upgrading => new(DeviceStatusKind.Transitional, "Updating"),
        UniFiDeviceState.Provisioning => new(DeviceStatusKind.Transitional, "Provisioning"),
        UniFiDeviceState.Adopting => new(DeviceStatusKind.Transitional, "Adopting"),
        UniFiDeviceState.Deleting => new(DeviceStatusKind.Transitional, "Deleting"),

        UniFiDeviceState.AdoptionFailed => new(DeviceStatusKind.Error, "Adoption Failed"),
        UniFiDeviceState.Isolated => new(DeviceStatusKind.Error, "Isolated"),
        UniFiDeviceState.IncorrectTopology => new(DeviceStatusKind.Error, "Incorrect Topology"),

        // Disconnected (0), HeartbeatMissed (6), InformError (9), and anything unrecognized.
        _ => new(DeviceStatusKind.Offline, "Offline")
    };

    /// <summary>
    /// Whether a device in this state is fully online and actionable (the Online bucket). True for
    /// Connected and Update-Available; false for transient, offline, and error states.
    /// </summary>
    public static bool IsOnline(int state) => ToStatus(state).IsOnline;
}
