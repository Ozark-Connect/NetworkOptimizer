namespace NetworkOptimizer.Core.Enums;

/// <summary>
/// Represents the types of UniFi network devices that can be managed and audited.
/// </summary>
public enum DeviceType
{
    /// <summary>
    /// Unknown or unidentified device type.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// UniFi Security Gateway (USG) or Dream Machine series.
    /// </summary>
    Gateway = 1,

    /// <summary>
    /// UniFi managed network switch.
    /// </summary>
    Switch = 2,

    /// <summary>
    /// UniFi wireless access point.
    /// </summary>
    AccessPoint = 3,

    /// <summary>
    /// UniFi Protect NVR or camera.
    /// </summary>
    ProtectDevice = 4,

    /// <summary>
    /// UniFi Talk VoIP device.
    /// </summary>
    TalkDevice = 5,

    /// <summary>
    /// UniFi Access control device.
    /// </summary>
    AccessDevice = 6
}
