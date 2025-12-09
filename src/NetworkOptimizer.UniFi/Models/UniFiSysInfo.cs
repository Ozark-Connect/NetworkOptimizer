using System.Text.Json.Serialization;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// Response from GET /api/s/{site}/stat/sysinfo
/// Contains controller system information including licensing fingerprint
/// </summary>
public class UniFiSysInfo
{
    [JsonPropertyName("_id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("site_id")]
    public string SiteId { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("previous_version")]
    public string? PreviousVersion { get; set; }

    [JsonPropertyName("build")]
    public string Build { get; set; } = string.Empty;

    [JsonPropertyName("update_available")]
    public bool UpdateAvailable { get; set; }

    [JsonPropertyName("update_downloaded")]
    public bool UpdateDownloaded { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = string.Empty;

    [JsonPropertyName("ip_addrs")]
    public List<string> IpAddrs { get; set; } = new();

    [JsonPropertyName("inform_ip")]
    public string InformIp { get; set; } = string.Empty;

    [JsonPropertyName("inform_url")]
    public string InformUrl { get; set; } = string.Empty;

    [JsonPropertyName("udm_version")]
    public string? UdmVersion { get; set; }

    [JsonPropertyName("ubnt_device_type")]
    public string? UbntDeviceType { get; set; }

    [JsonPropertyName("console_display_version")]
    public string? ConsoleDisplayVersion { get; set; }

    // Cloud connectivity
    [JsonPropertyName("cloud_key_type")]
    public string? CloudKeyType { get; set; }

    [JsonPropertyName("cloud_key_name")]
    public string? CloudKeyName { get; set; }

    [JsonPropertyName("cloud_key_running")]
    public bool CloudKeyRunning { get; set; }

    [JsonPropertyName("cloud_key_available")]
    public bool CloudKeyAvailable { get; set; }

    // Hardware info
    [JsonPropertyName("hardware_model")]
    public string? HardwareModel { get; set; }

    [JsonPropertyName("hardware_version")]
    public string? HardwareVersion { get; set; }

    // Timezone
    [JsonPropertyName("timezone")]
    public string Timezone { get; set; } = string.Empty;

    [JsonPropertyName("timezone_offset")]
    public int TimezoneOffset { get; set; }

    // Uptime
    [JsonPropertyName("uptime")]
    public long Uptime { get; set; }

    // Licensing and fingerprinting - CRITICAL for controller identification
    [JsonPropertyName("anonymous_controller_id")]
    public string? AnonymousControllerId { get; set; }

    [JsonPropertyName("anonymous_device_id")]
    public string? AnonymousDeviceId { get; set; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; set; }

    [JsonPropertyName("unifi_go_enabled")]
    public bool UnifiGoEnabled { get; set; }

    // Database
    [JsonPropertyName("db_size")]
    public long DbSize { get; set; }

    // Live updates
    [JsonPropertyName("live_chat")]
    public string? LiveChat { get; set; }

    [JsonPropertyName("store_enabled")]
    public string? StoreEnabled { get; set; }

    // Stats
    [JsonPropertyName("data_retention_time_in_hours_for_5minutes_scale")]
    public int DataRetentionTimeInHoursFor5MinutesScale { get; set; }

    [JsonPropertyName("data_retention_time_in_hours_for_hourly_scale")]
    public int DataRetentionTimeInHoursForHourlyScale { get; set; }

    [JsonPropertyName("data_retention_time_in_hours_for_daily_scale")]
    public int DataRetentionTimeInHoursForDailyScale { get; set; }

    [JsonPropertyName("data_retention_time_in_hours_for_monthly_scale")]
    public int DataRetentionTimeInHoursForMonthlyScale { get; set; }

    // Features
    [JsonPropertyName("autobackup")]
    public bool Autobackup { get; set; }

    [JsonPropertyName("autobackup_days")]
    public int AutobackupDays { get; set; }

    [JsonPropertyName("override_inform_host")]
    public bool OverrideInformHost { get; set; }

    [JsonPropertyName("image_maps_use_google_engine")]
    public bool ImageMapsUseGoogleEngine { get; set; }

    [JsonPropertyName("radius_disconnect_running")]
    public bool RadiusDisconnectRunning { get; set; }

    // Facebook WiFi
    [JsonPropertyName("facebook_wifi_registered")]
    public bool FacebookWifiRegistered { get; set; }

    // Geolocation
    [JsonPropertyName("geolocation_lat")]
    public string? GeolocationLat { get; set; }

    [JsonPropertyName("geolocation_lng")]
    public string? GeolocationLng { get; set; }

    // Controller public IP
    [JsonPropertyName("public_ip")]
    public string? PublicIp { get; set; }

    // Default site ID
    [JsonPropertyName("default_site_id")]
    public string? DefaultSiteId { get; set; }
}

/// <summary>
/// Wrapper response for sysinfo endpoint - UniFi returns data in a "data" array
/// </summary>
public class UniFiSysInfoResponse
{
    [JsonPropertyName("meta")]
    public UniFiMeta Meta { get; set; } = new();

    [JsonPropertyName("data")]
    public List<UniFiSysInfo> Data { get; set; } = new();
}

// UniFiMeta is defined in UniFiApiResponse.cs
