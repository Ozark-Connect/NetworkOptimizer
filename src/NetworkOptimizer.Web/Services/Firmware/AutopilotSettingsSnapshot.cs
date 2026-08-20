using System.Text.Json;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// Round-trips the standing Autopilot configuration through the settings column that holds it.
/// Serializing the entity itself is what makes this safe to evolve: a field added to
/// <see cref="FirmwareRolloutSettings"/> is captured without touching this file, which is why the
/// snapshot is not a hand-maintained DTO and not built in SQL.
/// </summary>
public static class AutopilotSettingsSnapshot
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <summary>Captures settings as the standing Autopilot configuration.</summary>
    /// <param name="settings">Settings to capture.</param>
    public static string Serialize(FirmwareRolloutSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // AutopilotSettingsJson carries [JsonIgnore], so a capture never nests the previous one.
        return JsonSerializer.Serialize(settings, Options);
    }

    /// <summary>
    /// Reads a captured configuration back, or null when there is none or it is unreadable.
    /// The row's own identity is not restored - the caller is updating the existing row.
    /// </summary>
    /// <param name="snapshotJson">The stored snapshot.</param>
    public static FirmwareRolloutSettings? Deserialize(string? snapshotJson)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson)) return null;

        try
        {
            var restored = JsonSerializer.Deserialize<FirmwareRolloutSettings>(snapshotJson, Options);
            if (restored == null) return null;

            restored.Id = 0;
            return restored;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
