using System.Text.Json;
using System.Text.Json.Serialization;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// The `super_fwupdate` section of GET rest/setting: which release channel UniFi devices follow,
/// plus the channel options the console offers.
/// <para>
/// `x_sso_token` is deliberately not modeled. It is credential material, so it is neither read nor
/// written back - never add it, and never widen this to a whole-section round-trip.
/// </para>
/// </summary>
[VendorSpecific("UniFi", "rest/setting super_fwupdate section")]
public class UniFiFirmwareUpdateSettings
{
    /// <summary>The settings section key this model represents.</summary>
    public const string SettingKey = "super_fwupdate";

    [JsonPropertyName("_id")]
    public string? Id { get; set; }

    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("sso_enabled")]
    [JsonConverter(typeof(FlexibleNullableBoolConverter))]
    public bool? SsoEnabled { get; set; }

    /// <summary>Channel UniFi devices follow: "release" (GA), "release-candidate", "beta" (EA).</summary>
    [JsonPropertyName("firmware_channel")]
    public string? FirmwareChannel { get; set; }

    /// <summary>Device-channel options this console offers.</summary>
    [JsonPropertyName("available_firmware_channels")]
    public List<string> AvailableFirmwareChannels { get; set; } = new();

    /// <summary>UniFi Network application channel options this console offers.</summary>
    [JsonPropertyName("available_controller_channels")]
    public List<string> AvailableControllerChannels { get; set; } = new();

    /// <summary>
    /// Extracts the `super_fwupdate` section from a rest/setting response
    /// (`{"meta":{...},"data":[...sections]}`). Returns null when the section is absent.
    /// </summary>
    public static UniFiFirmwareUpdateSettings? FromSettingsResponse(JsonDocument settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var section in data.EnumerateArray())
        {
            if (section.ValueKind != JsonValueKind.Object)
                continue;

            if (section.TryGetProperty("key", out var key)
                && key.ValueKind == JsonValueKind.String
                && key.GetString() == SettingKey)
            {
                return section.Deserialize<UniFiFirmwareUpdateSettings>();
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the POST set/setting/super_fwupdate body for a channel change: a read-modify-write
    /// that carries the existing `_id` and `sso_enabled` back unchanged.
    /// <para>
    /// Only this section is ever written. The UniFi UI also re-POSTs the `mgmt` section when saving
    /// its page, which re-sends SSH credentials - we must never do that.
    /// </para>
    /// </summary>
    public static Dictionary<string, object?> BuildChannelWriteBody(
        UniFiFirmwareUpdateSettings current,
        string channel)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);

        var body = new Dictionary<string, object?> { ["key"] = SettingKey };

        if (current.SsoEnabled.HasValue)
            body["sso_enabled"] = current.SsoEnabled.Value;

        body["firmware_channel"] = channel;

        if (!string.IsNullOrEmpty(current.Id))
            body["_id"] = current.Id;

        return body;
    }
}
