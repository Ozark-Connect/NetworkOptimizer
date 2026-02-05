namespace NetworkOptimizer.WiFi.Models;

/// <summary>
/// WLAN (SSID) configuration with current statistics
/// </summary>
public class WlanConfiguration
{
    /// <summary>WLAN configuration ID</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>SSID name</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether the WLAN is enabled</summary>
    public bool Enabled { get; set; }

    /// <summary>Whether this is a guest network</summary>
    public bool IsGuest { get; set; }

    /// <summary>Whether SSID is hidden</summary>
    public bool HideSsid { get; set; }

    /// <summary>Security type (wpa2, wpa3, open, etc.)</summary>
    public string? Security { get; set; }

    /// <summary>Enabled bands for this WLAN</summary>
    public List<RadioBand> EnabledBands { get; set; } = new();

    /// <summary>Whether fast roaming (802.11r) is enabled</summary>
    public bool FastRoamingEnabled { get; set; }

    /// <summary>Whether BSS transition (802.11v) is enabled</summary>
    public bool BssTransitionEnabled { get; set; }

    /// <summary>Whether L2 isolation is enabled</summary>
    public bool L2IsolationEnabled { get; set; }

    /// <summary>
    /// Whether OUI-based 2.4GHz blocking is enabled (no2ghz_oui).
    /// When true, devices with known 5GHz-capable OUIs are blocked from 2.4GHz,
    /// effectively steering them to 5GHz. This is UniFi's band steering mechanism.
    /// </summary>
    public bool BandSteeringEnabled { get; set; }

    /// <summary>
    /// Whether Multi-Link Operation (MLO) is enabled for Wi-Fi 7.
    /// When enabled, capable devices can aggregate multiple bands simultaneously.
    /// Note: MLO has been observed to reduce throughput for non-MLO devices
    /// on 5 GHz and 6 GHz bands, even on APs not serving MLO clients.
    /// </summary>
    public bool MloEnabled { get; set; }

    /// <summary>Minimum data rate settings</summary>
    public MinRateSettings? MinRateSettings { get; set; }

    // Current statistics

    /// <summary>Current client count</summary>
    public int CurrentClientCount { get; set; }

    /// <summary>Current AP count broadcasting this SSID</summary>
    public int CurrentApCount { get; set; }

    /// <summary>Current satisfaction score (0-100)</summary>
    public int? CurrentSatisfaction { get; set; }

    /// <summary>Peak client count (today)</summary>
    public int? PeakClientCount { get; set; }

    /// <summary>
    /// Network ID that this WLAN is bound to.
    /// Links the WLAN to its associated network/VLAN.
    /// </summary>
    public string? NetworkId { get; set; }
}

/// <summary>
/// Minimum data rate settings for a WLAN
/// </summary>
public class MinRateSettings
{
    /// <summary>Whether min rate is enabled for 2.4 GHz</summary>
    public bool Enabled2_4GHz { get; set; }

    /// <summary>Min rate for 2.4 GHz in Kbps</summary>
    public int? MinRate2_4GHz { get; set; }

    /// <summary>Whether min rate is enabled for 5 GHz</summary>
    public bool Enabled5GHz { get; set; }

    /// <summary>Min rate for 5 GHz in Kbps</summary>
    public int? MinRate5GHz { get; set; }

    /// <summary>Whether to advertise lower rates</summary>
    public bool AdvertiseLowerRates { get; set; }
}
