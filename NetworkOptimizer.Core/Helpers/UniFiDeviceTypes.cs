namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// Centralized UniFi device type detection and classification.
/// Maps UniFi API type codes to device categories.
/// </summary>
public static class UniFiDeviceTypes
{
    /// <summary>
    /// Gateway device type codes (UDM, USG, UXG, UCG series)
    /// </summary>
    private static readonly HashSet<string> GatewayTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "udm",  // Dream Machine series
        "ugw",  // UniFi Security Gateway (USG)
        "uxg",  // Next-Gen Gateway
        "ucg"   // Cloud Gateway (UCG-Ultra, UCG-Fiber, etc.)
    };

    /// <summary>
    /// Switch device type codes
    /// </summary>
    private static readonly HashSet<string> SwitchTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "usw"   // UniFi Switch
    };

    /// <summary>
    /// Access Point device type codes
    /// </summary>
    private static readonly HashSet<string> AccessPointTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "uap"   // UniFi Access Point
    };

    /// <summary>
    /// Cellular Modem device type codes
    /// </summary>
    private static readonly HashSet<string> CellularModemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "umbb"  // UniFi Mobile Broadband
    };

    /// <summary>
    /// Check if the device type code represents a gateway
    /// </summary>
    public static bool IsGateway(string? type) =>
        !string.IsNullOrEmpty(type) && GatewayTypes.Contains(type);

    /// <summary>
    /// Check if the device type code represents a switch
    /// </summary>
    public static bool IsSwitch(string? type) =>
        !string.IsNullOrEmpty(type) && SwitchTypes.Contains(type);

    /// <summary>
    /// Check if the device type code represents an access point
    /// </summary>
    public static bool IsAccessPoint(string? type) =>
        !string.IsNullOrEmpty(type) && AccessPointTypes.Contains(type);

    /// <summary>
    /// Check if the device type code represents a cellular modem
    /// </summary>
    public static bool IsCellularModem(string? type) =>
        !string.IsNullOrEmpty(type) && CellularModemTypes.Contains(type);

    /// <summary>
    /// Get a friendly display name for a device type code
    /// </summary>
    public static string GetDisplayName(string? type) => type?.ToLowerInvariant() switch
    {
        "udm" or "ugw" or "uxg" or "ucg" => "Gateway",
        "usw" => "Switch",
        "uap" => "Access Point",
        "umbb" => "Cellular Modem",
        _ => type ?? "Unknown"
    };
}
