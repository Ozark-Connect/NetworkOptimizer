using System.Text.Json;
using System.Text.Json.Serialization;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// The view of the `mgmt` section of GET rest/setting. Only the auto-upgrade flag is modeled,
/// because that is UniFi's own nightly device upgrade and it races a rollout.
/// <para>
/// This section is NEVER round-tripped: it carries the site's SSH credentials, so writing it back
/// would re-send them. The one write is <see cref="BuildAutoUpgradeWriteBody"/>, a partial body
/// naming only `auto_upgrade`; the console leaves every other field as it was (live-tested,
/// including over an API key). Do not widen that body or this model.
/// </para>
/// </summary>
[VendorSpecific("UniFi", "rest/setting mgmt section; auto_upgrade-only partial write")]
public class UniFiMgmtSettings
{
    /// <summary>The settings section key this model represents.</summary>
    public const string SettingKey = "mgmt";

    /// <summary>
    /// The POST set/setting/mgmt body that sets the auto-upgrade flag and nothing else. Every
    /// mgmt save rotates `x_api_token`, the UI's own included; nothing in this app reads it.
    /// </summary>
    /// <param name="enabled">Whether UniFi upgrades devices on its own schedule.</param>
    public static Dictionary<string, object> BuildAutoUpgradeWriteBody(bool enabled) => new()
    {
        ["key"] = SettingKey,
        ["auto_upgrade"] = enabled,
    };

    /// <summary>Whether the console upgrades devices on its own schedule.</summary>
    [JsonPropertyName("auto_upgrade")]
    [JsonConverter(typeof(FlexibleNullableBoolConverter))]
    public bool? AutoUpgrade { get; set; }

    /// <summary>
    /// Extracts the `mgmt` section from a rest/setting response
    /// (`{"meta":{...},"data":[...sections]}`). Returns null when the section is absent.
    /// </summary>
    /// <param name="settings">The rest/setting response.</param>
    public static UniFiMgmtSettings? FromSettingsResponse(JsonDocument settings)
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
                return section.Deserialize<UniFiMgmtSettings>();
            }
        }

        return null;
    }
}
