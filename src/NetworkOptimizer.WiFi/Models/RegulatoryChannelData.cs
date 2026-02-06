using System.Text.Json;

namespace NetworkOptimizer.WiFi.Models;

/// <summary>
/// Regulatory channel availability data from UniFi stat/current-channel endpoint.
/// Contains per-band, per-width channel lists for the site's regulatory domain.
/// </summary>
public class RegulatoryChannelData
{
    /// <summary>2.4 GHz channels by width (20, 40)</summary>
    public Dictionary<int, int[]> Channels2_4GHz { get; set; } = new();

    /// <summary>5 GHz channels by width (20, 40, 80, 160, 240)</summary>
    public Dictionary<int, int[]> Channels5GHz { get; set; } = new();

    /// <summary>5 GHz DFS channels (subset of 5 GHz that require DFS)</summary>
    public int[] DfsChannels { get; set; } = [];

    /// <summary>6 GHz channels by width (20, 40, 80, 160, 320)</summary>
    public Dictionary<int, int[]> Channels6GHz { get; set; } = new();

    /// <summary>6 GHz PSC (Preferred Scanning Channels) - the channels UniFi UI shows in dropdown</summary>
    public int[] PscChannels6GHz { get; set; } = [];

    /// <summary>
    /// Get available channels for a band at a specific width.
    /// For 5 GHz, optionally excludes DFS channels.
    /// For 6 GHz, filters to PSC channels intersected with width-valid channels.
    /// </summary>
    public int[] GetChannels(RadioBand band, int width, bool includeDfs = true)
    {
        var dict = band switch
        {
            RadioBand.Band2_4GHz => Channels2_4GHz,
            RadioBand.Band5GHz => Channels5GHz,
            RadioBand.Band6GHz => Channels6GHz,
            _ => null
        };

        if (dict == null) return [];

        // Try exact width match, then fall back to base (20 MHz)
        if (!dict.TryGetValue(width, out var channels))
            if (!dict.TryGetValue(20, out channels))
                return [];

        if (band == RadioBand.Band5GHz && !includeDfs && DfsChannels.Length > 0)
        {
            var dfsSet = new HashSet<int>(DfsChannels);
            return channels.Where(ch => !dfsSet.Contains(ch)).ToArray();
        }

        // 6 GHz: filter to PSC channels (matches UniFi UI dropdown)
        if (band == RadioBand.Band6GHz && PscChannels6GHz.Length > 0)
        {
            var pscSet = new HashSet<int>(PscChannels6GHz);
            return channels.Where(ch => pscSet.Contains(ch)).ToArray();
        }

        return channels;
    }

    /// <summary>
    /// Parse from the UniFi stat/current-channel API response.
    /// Expects the first element of the "data" array.
    /// </summary>
    public static RegulatoryChannelData? Parse(JsonElement dataElement)
    {
        var result = new RegulatoryChannelData();

        // 2.4 GHz
        result.Channels2_4GHz[20] = ParseChannelArray(dataElement, "channels_ng");
        result.Channels2_4GHz[40] = ParseChannelArray(dataElement, "channels_ng_40");

        // 5 GHz
        result.Channels5GHz[20] = ParseChannelArray(dataElement, "channels_na");
        result.Channels5GHz[40] = ParseChannelArray(dataElement, "channels_na_40");
        result.Channels5GHz[80] = ParseChannelArray(dataElement, "channels_na_80");
        result.Channels5GHz[160] = ParseChannelArray(dataElement, "channels_na_160");
        result.Channels5GHz[240] = ParseChannelArray(dataElement, "channels_na_240");
        result.DfsChannels = ParseChannelArray(dataElement, "channels_na_dfs");

        // 6 GHz
        result.Channels6GHz[20] = ParseChannelArray(dataElement, "channels_6e");
        result.Channels6GHz[40] = ParseChannelArray(dataElement, "channels_6e_40");
        result.Channels6GHz[80] = ParseChannelArray(dataElement, "channels_6e_80");
        result.Channels6GHz[160] = ParseChannelArray(dataElement, "channels_6e_160");
        result.Channels6GHz[320] = ParseChannelArray(dataElement, "channels_6e_320");
        result.PscChannels6GHz = ParseChannelArray(dataElement, "channels_6e_psc");

        return result;
    }

    private static int[] ParseChannelArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            return [];

        return array.EnumerateArray()
            .Select(ch => ch.ValueKind == JsonValueKind.Number ? ch.GetInt32() : 0)
            .Where(ch => ch > 0)
            .ToArray();
    }
}
