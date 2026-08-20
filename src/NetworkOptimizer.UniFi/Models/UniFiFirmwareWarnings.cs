using System.Text.Json;
using System.Text.Json.Serialization;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// GET stat/widget/warnings - the console's own pre-flight signals for a firmware run.
/// <para>
/// Optional by design: the shape varies across UniFi Network versions, so every field is nullable
/// and <see cref="TryParse"/> returns null rather than throwing. Callers must treat an absent
/// result as "no information", never as a failure.
/// </para>
/// </summary>
[VendorSpecific("UniFi", "stat/widget/warnings; shape varies by Network version")]
public class UniFiFirmwareWarnings
{
    [JsonPropertyName("has_upgradable_devices")]
    [JsonConverter(typeof(FlexibleNullableBoolConverter))]
    public bool? HasUpgradableDevices { get; set; }

    [JsonPropertyName("unsupported_device_count")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? UnsupportedDeviceCount { get; set; }

    [JsonPropertyName("eol_device_count")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? EolDeviceCount { get; set; }

    [JsonPropertyName("lts_device_count")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? LtsDeviceCount { get; set; }

    /// <summary>Useful pre-flight-backup warning signal.</summary>
    [JsonPropertyName("controller_low_disk_space")]
    [JsonConverter(typeof(FlexibleNullableBoolConverter))]
    public bool? ControllerLowDiskSpace { get; set; }

    /// <summary>"ok" when the console's last firmware-catalog query succeeded.</summary>
    [JsonPropertyName("last_firmware_update_query_status")]
    public string? LastFirmwareUpdateQueryStatus { get; set; }

    /// <summary>
    /// Parses the widget out of a `{"meta":{...},"data":[{...}]}` response. Returns null for
    /// anything that is not that shape - non-JSON, a missing or empty data array, a data entry that
    /// is not an object, or a field whose type this version does not recognize.
    /// </summary>
    public static UniFiFirmwareWarnings? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                return entry.Deserialize<UniFiFirmwareWarnings>();
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
