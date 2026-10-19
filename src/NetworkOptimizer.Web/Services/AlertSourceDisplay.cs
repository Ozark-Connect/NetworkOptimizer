namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The display name of an alert's source id, shared by the Alerts page and the Dashboard's Active
/// Alerts card. Names match the Alerts page's History source filter.
/// </summary>
public static class AlertSourceDisplay
{
    /// <summary>The display name for a source id; an id with no name here shows as it is stored.</summary>
    public static string GetName(string? source) => source switch
    {
        "agent" => "Agent",
        "audit" => "Audit",
        "cable_modem" => "Cable Modem",
        "cellular" => "Cellular",
        "console" => "UniFi Console",
        "device" => "Device",
        "rollout" => "Firmware Rollout",
        "monitoring" => "Monitoring",
        "ont" => "ONT",
        "schedule" => "Schedule",
        "speedtest" => "Speed Test",
        "starlink" => "Starlink",
        "threats" => "Threats",
        "wan" => "WAN",
        "wifi" => "Wi-Fi",
        _ => source ?? string.Empty
    };
}
