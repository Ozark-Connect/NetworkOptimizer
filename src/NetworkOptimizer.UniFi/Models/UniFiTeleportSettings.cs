using System.Text.Json;
using System.Text.Json.Serialization;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// The view of the `teleport` section of GET rest/setting. Teleport's client range is not a
/// network in networkconf and no firewall zone lists it, so this section is the only place its
/// subnet appears.
/// </summary>
[VendorSpecific("UniFi", "rest/setting teleport section")]
public class UniFiTeleportSettings
{
    /// <summary>The settings section key this model represents.</summary>
    public const string SettingKey = "teleport";

    /// <summary>Whether Teleport is on.</summary>
    [JsonPropertyName("enabled")]
    [JsonConverter(typeof(FlexibleNullableBoolConverter))]
    public bool? Enabled { get; set; }

    /// <summary>The client range as UniFi stores it, gateway address and prefix ("192.168.2.1/24").</summary>
    [JsonPropertyName("subnet_cidr")]
    public string? SubnetCidr { get; set; }

    /// <summary>
    /// Extracts the `teleport` section from a rest/setting response
    /// (`{"meta":{...},"data":[...sections]}`). Returns null when the section is absent.
    /// </summary>
    /// <param name="settings">The rest/setting response.</param>
    public static UniFiTeleportSettings? FromSettingsResponse(JsonDocument settings)
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
                return section.Deserialize<UniFiTeleportSettings>();
            }
        }

        return null;
    }
}
