namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Device type constants for speed test devices.
/// Stored as strings in the database for readability.
/// </summary>
public static class DeviceTypes
{
    // UniFi device types
    public const string Gateway = "Gateway";
    public const string Switch = "Switch";
    public const string AccessPoint = "AccessPoint";

    // Non-UniFi device types
    public const string Server = "Server";
    public const string Desktop = "Desktop";

    /// <summary>
    /// All valid device types for UI dropdowns
    /// </summary>
    public static readonly string[] All = [Gateway, Switch, AccessPoint, Server, Desktop];

    /// <summary>
    /// UniFi device types (use UniFi parallel streams setting)
    /// </summary>
    public static readonly string[] UniFiTypes = [Switch, AccessPoint];

    /// <summary>
    /// Check if a device type is a UniFi device (Switch or AccessPoint)
    /// </summary>
    public static bool IsUniFi(string? deviceType) =>
        string.Equals(deviceType, Switch, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(deviceType, AccessPoint, StringComparison.OrdinalIgnoreCase);

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
            "udm" or "ugw" or "uxg" => Gateway,
            _ => Server
        };
    }
}
