using System.Text.Json;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.UniFi.Helpers;

/// <summary>
/// Resolved global switch settings with exclusion-aware device lookups.
/// Devices listed in switch_exclusions use their own device-level settings;
/// all other devices inherit from the global values.
/// </summary>
public class GlobalSwitchSettings
{
    public bool JumboFramesEnabled { get; init; }
    public bool FlowControlEnabled { get; init; }
    private HashSet<string> ExcludedMacs { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Parse from settings JSON (the root response from GetSettingsRawAsync).
    /// Looks for the "global_switch" object in the data array.
    /// Returns null if settings unavailable.
    /// </summary>
    public static GlobalSwitchSettings? FromSettingsJson(JsonDocument? settingsData)
    {
        if (settingsData == null)
            return null;

        if (!settingsData.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("key", out var key) ||
                key.GetString() != "global_switch")
                continue;

            bool jumbo = item.TryGetProperty("jumboframe_enabled", out var jf) &&
                         jf.ValueKind == JsonValueKind.True;

            bool flowCtrl = item.TryGetProperty("flowctrl_enabled", out var fc) &&
                            fc.ValueKind == JsonValueKind.True;

            var excludedMacs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (item.TryGetProperty("switch_exclusions", out var exclusions) &&
                exclusions.ValueKind == JsonValueKind.Array)
            {
                foreach (var mac in exclusions.EnumerateArray())
                {
                    var macStr = mac.GetString();
                    if (!string.IsNullOrEmpty(macStr))
                        excludedMacs.Add(macStr);
                }
            }

            return new GlobalSwitchSettings
            {
                JumboFramesEnabled = jumbo,
                FlowControlEnabled = flowCtrl,
                ExcludedMacs = excludedMacs
            };
        }

        return null;
    }

    /// <summary>
    /// Get the effective jumbo frames setting for a specific device.
    /// If device MAC is excluded from global settings, uses device-level value.
    /// Otherwise uses the global value.
    /// </summary>
    public bool GetEffectiveJumboFrames(UniFiDeviceResponse device)
    {
        if (ExcludedMacs.Contains(device.Mac))
            return device.JumboFrameEnabled == true;
        return JumboFramesEnabled;
    }

    /// <summary>
    /// Get the effective flow control setting for a specific device.
    /// If device MAC is excluded from global settings, uses device-level value.
    /// Otherwise uses the global value.
    /// </summary>
    public bool GetEffectiveFlowControl(UniFiDeviceResponse device)
    {
        if (ExcludedMacs.Contains(device.Mac))
            return device.FlowControlEnabled == true;
        return FlowControlEnabled;
    }

    /// <summary>
    /// Whether a device MAC is in the exclusion list (uses device-specific settings).
    /// </summary>
    public bool IsExcluded(string mac) => ExcludedMacs.Contains(mac);

    /// <summary>
    /// Get all excluded MAC addresses.
    /// </summary>
    public IReadOnlyCollection<string> GetExcludedMacs() => ExcludedMacs;
}
