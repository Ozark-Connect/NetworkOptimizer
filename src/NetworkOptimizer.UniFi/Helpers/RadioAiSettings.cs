using System.Text.Json;
using System.Text.Json.Serialization;
using NetworkOptimizer.Core;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.UniFi.Helpers;

/// <summary>
/// A single per-radio entry from the UniFi "radio_ai" setting's radios_configuration array.
/// </summary>
[VendorSpecific("UniFi", "Parses radio_ai.radios_configuration entries from UniFi settings JSON")]
public class RadioAiRadioConfig
{
    /// <summary>
    /// Radio band identifier: "ng" (2.4 GHz), "na" (5 GHz), "6e" (6 GHz).
    /// </summary>
    [JsonPropertyName("radio")]
    public string? Radio { get; set; }

    /// <summary>
    /// Whether the console's Auto-Optimize (RF Scanning) feature is allowed to use
    /// DFS channels on this radio. May arrive as a bool, string, or 0/1 number.
    /// </summary>
    [JsonPropertyName("dfs")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool Dfs { get; set; }

    /// <summary>
    /// Configured channel width (MHz) for this radio.
    /// </summary>
    [JsonPropertyName("channel_width")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? ChannelWidth { get; set; }
}

/// <summary>
/// Resolved UniFi "radio_ai" (Auto-Optimize / RF Scanning) settings.
/// Exposes the per-radio DFS preference so the channel recommender can default
/// its DFS toggle to match how the console is configured.
/// </summary>
[VendorSpecific("UniFi", "Parses UniFi settings JSON 'data' array with 'key' discriminator (radio_ai)")]
public class RadioAiSettings
{
    /// <summary>UniFi radio identifier for the 5 GHz band.</summary>
    public const string Radio5GHz = "na";

    private IReadOnlyList<RadioAiRadioConfig> RadiosConfiguration { get; init; } = Array.Empty<RadioAiRadioConfig>();

    /// <summary>
    /// Parse from settings JSON (the root response from GetSettingsRawAsync).
    /// Looks for the "radio_ai" object in the data array.
    /// Returns null if settings unavailable or the radio_ai key is absent.
    /// </summary>
    public static RadioAiSettings? FromSettingsJson(JsonDocument? settingsData)
    {
        if (settingsData == null)
            return null;

        if (!settingsData.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("key", out var key) ||
                key.GetString() != "radio_ai")
                continue;

            var radios = new List<RadioAiRadioConfig>();
            if (item.TryGetProperty("radios_configuration", out var radiosConfig) &&
                radiosConfig.ValueKind == JsonValueKind.Array)
            {
                foreach (var radio in radiosConfig.EnumerateArray())
                {
                    var parsed = radio.Deserialize<RadioAiRadioConfig>();
                    if (parsed != null)
                        radios.Add(parsed);
                }
            }

            return new RadioAiSettings { RadiosConfiguration = radios };
        }

        return null;
    }

    /// <summary>
    /// Whether DFS channels are enabled in the console's Auto-Optimize configuration
    /// for the given radio (e.g. "na" for 5 GHz). Returns null when the radio is absent
    /// so callers can fall back to their own default.
    /// </summary>
    public bool? GetDfsEnabled(string radio)
    {
        foreach (var cfg in RadiosConfiguration)
        {
            if (string.Equals(cfg.Radio, radio, StringComparison.OrdinalIgnoreCase))
                return cfg.Dfs;
        }
        return null;
    }
}
