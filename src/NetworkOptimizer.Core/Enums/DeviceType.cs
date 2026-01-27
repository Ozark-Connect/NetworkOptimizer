namespace NetworkOptimizer.Core.Enums;

/// <summary>
/// Unified device type enum for all network devices.
/// Stored as string in database for readability and backwards compatibility.
/// </summary>
public enum DeviceType
{
    /// <summary>
    /// Unknown or unidentified device type.
    /// </summary>
    Unknown = 0,

    // === UniFi Network Infrastructure Devices ===

    /// <summary>
    /// UniFi Security Gateway (USG), Dream Machine, or Cloud Gateway series.
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
    /// UniFi LTE/5G cellular modem (Mobile Broadband).
    /// </summary>
    CellularModem = 4,

    /// <summary>
    /// UniFi Building-to-Building Bridge.
    /// </summary>
    BuildingBridge = 5,

    /// <summary>
    /// UniFi Cloud Key controller.
    /// </summary>
    CloudKey = 6,

    /// <summary>
    /// UniFi Device Bridge (UDB series).
    /// </summary>
    DeviceBridge = 7,

    // === UniFi Application Devices (Protect, Talk, Access) ===

    /// <summary>
    /// UniFi Protect NVR or camera.
    /// </summary>
    ProtectDevice = 10,

    /// <summary>
    /// UniFi Talk VoIP device.
    /// </summary>
    TalkDevice = 11,

    /// <summary>
    /// UniFi Access control device.
    /// </summary>
    AccessDevice = 12,

    // === Non-UniFi Devices (manually configured) ===

    /// <summary>
    /// Generic server (for speed testing).
    /// </summary>
    Server = 20,

    /// <summary>
    /// Desktop computer (for speed testing).
    /// </summary>
    Desktop = 21,

    /// <summary>
    /// Laptop computer (for speed testing).
    /// </summary>
    Laptop = 22
}

/// <summary>
/// Extension methods for DeviceType enum
/// </summary>
public static class DeviceTypeExtensions
{
    /// <summary>
    /// All device types available for UI dropdowns (excludes Unknown and application devices)
    /// </summary>
    public static readonly DeviceType[] AllForSpeedTest =
    [
        DeviceType.Gateway,
        DeviceType.Switch,
        DeviceType.AccessPoint,
        DeviceType.CellularModem,
        DeviceType.BuildingBridge,
        DeviceType.CloudKey,
        DeviceType.Server,
        DeviceType.Desktop,
        DeviceType.Laptop
    ];

    /// <summary>
    /// Get the user-friendly display name for a device type
    /// </summary>
    public static string ToDisplayName(this DeviceType type) => type switch
    {
        DeviceType.Gateway => "Gateway",
        DeviceType.Switch => "Switch",
        DeviceType.AccessPoint => "Access Point",
        DeviceType.CellularModem => "Cellular Modem",
        DeviceType.BuildingBridge => "Building Bridge",
        DeviceType.DeviceBridge => "Device Bridge",
        DeviceType.CloudKey => "Cloud Key",
        DeviceType.ProtectDevice => "Protect Device",
        DeviceType.TalkDevice => "Talk Device",
        DeviceType.AccessDevice => "Access Device",
        DeviceType.Server => "Server",
        DeviceType.Desktop => "Desktop",
        DeviceType.Laptop => "Laptop",
        _ => "Unknown"
    };

    /// <summary>
    /// Check if this is a UniFi network infrastructure device type
    /// </summary>
    public static bool IsUniFiNetworkDevice(this DeviceType type) => type switch
    {
        DeviceType.Gateway or
        DeviceType.Switch or
        DeviceType.AccessPoint or
        DeviceType.CellularModem or
        DeviceType.BuildingBridge or
        DeviceType.DeviceBridge or
        DeviceType.CloudKey => true,
        _ => false
    };

    /// <summary>
    /// Check if this is a gateway device type
    /// </summary>
    public static bool IsGateway(this DeviceType type) => type == DeviceType.Gateway;

    /// <summary>
    /// Check if this UniFi device type should use UniFi parallel streams setting for iperf3
    /// (excludes Gateway which has dedicated test, and CloudKey which isn't typically tested)
    /// </summary>
    public static bool UsesUniFiIperfStreams(this DeviceType type) => type switch
    {
        DeviceType.Switch or
        DeviceType.AccessPoint or
        DeviceType.CellularModem or
        DeviceType.BuildingBridge => true,
        _ => false
    };

    /// <summary>
    /// Parse UniFi API device type code to DeviceType enum.
    /// API returns codes like "uap", "usw", "udm", "ucg", "umbb", "ubb", "uck".
    /// </summary>
    public static DeviceType FromUniFiApiType(string? apiType)
    {
        if (string.IsNullOrEmpty(apiType))
            return DeviceType.Unknown;

        return apiType.ToLowerInvariant() switch
        {
            "ugw" or "usg" or "udm" or "uxg" or "ucg" => DeviceType.Gateway,
            "usw" => DeviceType.Switch,
            "uap" => DeviceType.AccessPoint,
            "umbb" => DeviceType.CellularModem,
            "ubb" => DeviceType.BuildingBridge,
            "udb" => DeviceType.DeviceBridge,
            "uck" => DeviceType.CloudKey,
            _ => DeviceType.Unknown
        };
    }

    /// <summary>
    /// Parse string to DeviceType enum (for database/config values).
    /// Matches enum name case-insensitively.
    /// </summary>
    public static DeviceType Parse(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return DeviceType.Unknown;

        if (Enum.TryParse<DeviceType>(value, ignoreCase: true, out var result))
            return result;

        return DeviceType.Unknown;
    }
}
