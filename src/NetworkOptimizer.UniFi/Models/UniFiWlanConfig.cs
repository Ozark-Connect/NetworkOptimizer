using System.Text.Json.Serialization;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// Response from GET /api/s/{site}/rest/wlanconf
/// Represents a WLAN (WiFi network) configuration
/// </summary>
public class UniFiWlanConfig
{
    [JsonPropertyName("_id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("is_guest")]
    public bool IsGuest { get; set; }

    [JsonPropertyName("hide_ssid")]
    public bool HideSsid { get; set; }

    [JsonPropertyName("security")]
    public string? Security { get; set; }

    /// <summary>
    /// MLO (Multi-Link Operation) - Wi-Fi 7 feature that allows simultaneous
    /// transmission across multiple bands for improved throughput.
    /// </summary>
    [JsonPropertyName("mlo_enabled")]
    public bool MloEnabled { get; set; }

    [JsonPropertyName("fast_roaming_enabled")]
    public bool FastRoamingEnabled { get; set; }

    [JsonPropertyName("bss_transition")]
    public bool BssTransition { get; set; }

    [JsonPropertyName("l2_isolation")]
    public bool L2Isolation { get; set; }

    /// <summary>
    /// Band steering - prefer 5GHz over 2.4GHz
    /// </summary>
    [JsonPropertyName("no2ghz_oui")]
    public bool No2ghzOui { get; set; }

    /// <summary>
    /// Enabled bands: ["2g", "5g", "6g"]
    /// </summary>
    [JsonPropertyName("wlan_bands")]
    public List<string>? WlanBands { get; set; }

    // Minimum rate settings
    [JsonPropertyName("minrate_ng_enabled")]
    public bool MinrateNgEnabled { get; set; }

    [JsonPropertyName("minrate_ng_data_rate_kbps")]
    public int MinrateNgDataRateKbps { get; set; }

    [JsonPropertyName("minrate_na_enabled")]
    public bool MinrateNaEnabled { get; set; }

    [JsonPropertyName("minrate_na_data_rate_kbps")]
    public int MinrateNaDataRateKbps { get; set; }

    [JsonPropertyName("minrate_ng_advertising_rates")]
    public bool MinrateNgAdvertisingRates { get; set; }

    [JsonPropertyName("minrate_na_advertising_rates")]
    public bool MinrateNaAdvertisingRates { get; set; }

    /// <summary>
    /// AP group mode: "all" means broadcast on all APs, otherwise check ap_group_ids
    /// </summary>
    [JsonPropertyName("ap_group_mode")]
    public string? ApGroupMode { get; set; }

    /// <summary>
    /// AP group IDs that broadcast this WLAN (when ap_group_mode != "all")
    /// </summary>
    [JsonPropertyName("ap_group_ids")]
    public List<string>? ApGroupIds { get; set; }
}
