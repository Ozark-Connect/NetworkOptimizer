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

    /// <summary>
    /// The device name to show beside an alert's source, or null when there is none or it only
    /// repeats the source's display name (an agent left at its default name, "Agent").
    /// </summary>
    public static string? GetDeviceName(string? source, string? deviceName) =>
        string.IsNullOrWhiteSpace(deviceName)
        || string.Equals(deviceName.Trim(), GetName(source), StringComparison.OrdinalIgnoreCase)
            ? null
            : deviceName;

    /// <summary>
    /// The label and value an incident's correlation key shows as: "source:rollout" is Source,
    /// Firmware Rollout; "device:192.0.2.1" is Device, 192.0.2.1. Any other key shows as stored.
    /// </summary>
    public static (string Label, string Value) DescribeCorrelationKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return ("Grouped By", string.Empty);
        var colon = key.IndexOf(':');
        if (colon > 0 && colon < key.Length - 1)
        {
            var value = key[(colon + 1)..];
            switch (key[..colon])
            {
                case "source": return ("Source", GetName(value));
                case "device": return ("Device", value);
            }
        }
        return ("Grouped By", key);
    }
}
