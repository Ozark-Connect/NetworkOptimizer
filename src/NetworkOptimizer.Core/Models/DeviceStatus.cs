namespace NetworkOptimizer.Core.Models;

/// <summary>
/// Coarse connection-status bucket for a managed device, independent of vendor. Drives the color
/// of the status indicator in the UI: green / yellow / grey / red.
/// </summary>
public enum DeviceStatusKind
{
    /// <summary>Connected and operating normally (green). Safe to run actions against.</summary>
    Online,

    /// <summary>
    /// A transient, in-progress state (provisioning, updating, adopting, pending) - not a fault,
    /// but not yet fully online either (yellow).
    /// </summary>
    Transitional,

    /// <summary>Not reachable by the controller (grey).</summary>
    Offline,

    /// <summary>A fault that needs attention - e.g. adoption failed, isolated (red).</summary>
    Error
}

/// <summary>
/// A device's connection status: a <see cref="DeviceStatusKind"/> bucket plus the specific
/// human-readable label for the underlying state (e.g. "Provisioning", "Update Available").
/// Map a vendor's raw state to this once, then drive every indicator off it.
/// </summary>
public readonly record struct DeviceStatus(DeviceStatusKind Kind, string Label)
{
    /// <summary>
    /// True only for the <see cref="DeviceStatusKind.Online"/> bucket. Use this to gate actions
    /// that require a fully-online device (e.g. running an optimization or speed test) - a device
    /// that is provisioning or updating is not offline, but also can't be acted on yet.
    /// </summary>
    public bool IsOnline => Kind == DeviceStatusKind.Online;

    /// <summary>True only for the <see cref="DeviceStatusKind.Offline"/> bucket (genuinely
    /// unreachable). Provisioning/updating/error states are NOT offline.</summary>
    public bool IsOffline => Kind == DeviceStatusKind.Offline;

    /// <summary>
    /// Status-dot CSS modifier (online / provisioning / offline / error). Transitional states all
    /// share the "provisioning" (yellow) dot.
    /// </summary>
    public string CssClass => Kind switch
    {
        DeviceStatusKind.Online => "online",
        DeviceStatusKind.Transitional => "provisioning",
        DeviceStatusKind.Error => "error",
        _ => "offline"
    };
}
