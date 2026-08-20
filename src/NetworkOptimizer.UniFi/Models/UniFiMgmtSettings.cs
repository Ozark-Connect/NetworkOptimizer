using System.Text.Json;
using System.Text.Json.Serialization;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// The read-only view of the `mgmt` section of GET rest/setting. Only the auto-upgrade flag is
/// modeled, because that is UniFi's own nightly device upgrade and it races a rollout.
/// <para>
/// This section is NEVER written back: it carries the site's SSH credentials, so a round-trip would
/// re-send them. Read only - do not add a write path or widen this model.
/// </para>
/// </summary>
[VendorSpecific("UniFi", "rest/setting mgmt section, read only")]
public class UniFiMgmtSettings
{
    /// <summary>The settings section key this model represents.</summary>
    public const string SettingKey = "mgmt";

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
