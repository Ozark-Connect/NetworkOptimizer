namespace NetworkOptimizer.Audit.Models;

/// <summary>
/// Settings for allowing certain device types on main/corporate networks
/// without triggering warnings. These devices will be reported as Informational
/// instead of Recommended when found on non-IoT networks.
/// </summary>
public class DeviceAllowanceSettings
{
    /// <summary>
    /// Allow Apple streaming devices (Apple TV) on main network.
    /// </summary>
    public bool AllowAppleStreamingOnMainNetwork { get; set; } = false;

    /// <summary>
    /// Allow all streaming devices (Apple TV, Roku, Fire TV, Chromecast) on main network.
    /// </summary>
    public bool AllowAllStreamingOnMainNetwork { get; set; } = false;

    /// <summary>
    /// Allow name-brand Smart TVs (LG, Samsung, Sony) on main network.
    /// </summary>
    public bool AllowNameBrandTVsOnMainNetwork { get; set; } = false;

    /// <summary>
    /// Allow all Smart TVs on main network.
    /// </summary>
    public bool AllowAllTVsOnMainNetwork { get; set; } = false;

    /// <summary>
    /// Allow printers on main network. When false, printers on main network
    /// will be flagged as Informational (should move to IoT or Printer VLAN).
    /// </summary>
    public bool AllowPrintersOnMainNetwork { get; set; } = true;

    /// <summary>
    /// Check if a streaming device should be allowed on main network based on vendor.
    /// </summary>
    public bool IsStreamingDeviceAllowed(string? vendor)
    {
        if (AllowAllStreamingOnMainNetwork)
            return true;

        if (AllowAppleStreamingOnMainNetwork &&
            !string.IsNullOrEmpty(vendor) &&
            vendor.Contains("Apple", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Check if a Smart TV should be allowed on main network based on vendor.
    /// </summary>
    public bool IsSmartTVAllowed(string? vendor)
    {
        if (AllowAllTVsOnMainNetwork)
            return true;

        if (AllowNameBrandTVsOnMainNetwork && !string.IsNullOrEmpty(vendor))
        {
            if (vendor.Contains("LG", StringComparison.OrdinalIgnoreCase) ||
                vendor.Contains("Samsung", StringComparison.OrdinalIgnoreCase) ||
                vendor.Contains("Sony", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Default settings with no allowances.
    /// </summary>
    public static DeviceAllowanceSettings Default => new();
}
