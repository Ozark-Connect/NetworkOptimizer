namespace NetworkOptimizer.Core.Enums;

/// <summary>
/// Device type constants for network devices.
/// Stored as strings in the database for readability.
/// </summary>
public static class DeviceTypes
{
    // UniFi device types
    public const string Gateway = "Gateway";
    public const string Switch = "Switch";
    public const string AccessPoint = "AccessPoint";
    public const string CellularModem = "CellularModem";

    // Non-UniFi device types
    public const string Server = "Server";
    public const string Desktop = "Desktop";

    /// <summary>
    /// All valid device types for UI dropdowns
    /// </summary>
    public static readonly string[] All = [Gateway, Switch, AccessPoint, CellularModem, Server, Desktop];

    /// <summary>
    /// UniFi device types (use UniFi parallel streams setting)
    /// </summary>
    public static readonly string[] UniFiTypes = [Switch, AccessPoint, CellularModem];

    /// <summary>
    /// Check if a device type is a UniFi device (Switch, AccessPoint, or CellularModem)
    /// </summary>
    public static bool IsUniFi(string? deviceType) =>
        string.Equals(deviceType, Switch, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(deviceType, AccessPoint, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(deviceType, CellularModem, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Check if a device type is a Gateway
    /// </summary>
    public static bool IsGateway(string? deviceType) =>
        string.Equals(deviceType, Gateway, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Get display name for a device type
    /// </summary>
    public static string GetDisplayName(string? deviceType) => deviceType switch
    {
        AccessPoint => "Access Point",
        CellularModem => "Cellular Modem",
        _ => deviceType ?? "Unknown"
    };

    /// <summary>
    /// Convert UniFi API device type code (uap, usw, udm, etc.) to DeviceTypes constant
    /// </summary>
    public static string FromUniFiType(string? unifiType)
    {
        if (string.IsNullOrEmpty(unifiType))
            return Server;

        return unifiType.ToLowerInvariant() switch
        {
            "uap" => AccessPoint,
            "usw" => Switch,
            "udm" or "ugw" or "uxg" or "ucg" => Gateway,
            "umbb" => CellularModem,
            _ => Server
        };
    }
}
