using System.Text.Json.Serialization;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// Response from the WiFiman client endpoint:
/// GET /v2/api/site/{site}/wifiman/{clientIp}/
/// </summary>
public class WiFiManClientResponse
{
    [JsonPropertyName("signal")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Signal { get; set; }

    [JsonPropertyName("noise")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Noise { get; set; }

    [JsonPropertyName("channel")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Channel { get; set; }

    [JsonPropertyName("channel_width")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? ChannelWidth { get; set; }

    [JsonPropertyName("radio_protocol")]
    public string? RadioProtocol { get; set; }

    /// <summary>
    /// Band code from WiFiman endpoint. Uses different codes than stat/sta:
    /// "6g" (6 GHz), "5g" (5 GHz), "2.4g" (2.4 GHz) — needs conversion to "6e"/"na"/"ng".
    /// </summary>
    [JsonPropertyName("wlan_band")]
    public string? WlanBand { get; set; }

    [JsonPropertyName("link_download_rate_kbps")]
    public long? LinkDownloadRateKbps { get; set; }

    [JsonPropertyName("link_upload_rate_kbps")]
    public long? LinkUploadRateKbps { get; set; }

    [JsonPropertyName("wifi_experience")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? WiFiExperience { get; set; }

    [JsonPropertyName("isp_download_capability")]
    public long? IspDownloadCapability { get; set; }

    [JsonPropertyName("isp_upload_capability")]
    public long? IspUploadCapability { get; set; }

    [JsonPropertyName("nearest_neighbors")]
    public List<WiFiManNeighbor>? NearestNeighbors { get; set; }

    [JsonPropertyName("statistics")]
    public List<WiFiManExperienceStat>? Statistics { get; set; }

    [JsonPropertyName("uplink_devices")]
    public List<WiFiManUplinkDevice>? UplinkDevices { get; set; }

    /// <summary>
    /// Convert WiFiman wlan_band code to UniFi radio code used throughout the app.
    /// </summary>
    public string? RadioCode => WlanBand?.ToLowerInvariant() switch
    {
        "6g" => "6e",
        "5g" => "na",
        "2.4g" => "ng",
        _ => WlanBand // pass through unknown values
    };
}

public class WiFiManNeighbor
{
    [JsonPropertyName("ap_mac")]
    public string? ApMac { get; set; }

    [JsonPropertyName("band")]
    public string? Band { get; set; }

    [JsonPropertyName("bssid")]
    public string? Bssid { get; set; }

    [JsonPropertyName("channel")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Channel { get; set; }

    [JsonPropertyName("channel_width")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? ChannelWidth { get; set; }

    [JsonPropertyName("icon_device_uidb_id")]
    public string? IconDeviceUidbId { get; set; }

    [JsonPropertyName("last_seen")]
    public long? LastSeen { get; set; }

    [JsonPropertyName("model_display")]
    public string? ModelDisplay { get; set; }

    [JsonPropertyName("radio_name")]
    public string? RadioName { get; set; }

    [JsonPropertyName("security")]
    public string? Security { get; set; }

    [JsonPropertyName("signal")]
    public List<WiFiManSignalEntry>? Signal { get; set; }
}

public class WiFiManSignalEntry
{
    [JsonPropertyName("signal")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Signal { get; set; }

    [JsonPropertyName("signal_type")]
    public string? SignalType { get; set; }
}

public class WiFiManExperienceStat
{
    [JsonPropertyName("experience")]
    public double? Experience { get; set; }

    [JsonPropertyName("time")]
    public long? Time { get; set; }
}

public class WiFiManUplinkDevice
{
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("experience")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Experience { get; set; }

    [JsonPropertyName("icon_engine_id")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? IconEngineId { get; set; }

    [JsonPropertyName("ip")]
    public string? Ip { get; set; }

    [JsonPropertyName("number_of_clients")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? NumberOfClients { get; set; }

    [JsonPropertyName("sku")]
    public string? Sku { get; set; }

    [JsonPropertyName("wireless_uplink")]
    public bool? WirelessUplink { get; set; }
}
