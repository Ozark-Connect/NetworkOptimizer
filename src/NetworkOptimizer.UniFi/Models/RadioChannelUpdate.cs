using System.Text.Json;
using System.Text.Json.Serialization;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// One radio's new channel and width, as sent in a <c>PUT rest/device/{id}</c> radio_table entry.
/// </summary>
/// <param name="Name">Radio name from the device's radio_table (wifi0, wifi1, ...); the console matches entries by it.</param>
/// <param name="Radio">Band code: ng (2.4 GHz), na (5 GHz), 6e (6 GHz).</param>
/// <param name="Channel">Primary channel number.</param>
/// <param name="Width">Channel width in MHz (the console's <c>ht</c>).</param>
public sealed record RadioChannelUpdate(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("radio")] string Radio,
    [property: JsonPropertyName("channel")] int Channel,
    [property: JsonPropertyName("ht")] int Width)
{
    /// <summary>
    /// The minimal PUT body: only the radios being moved, four fields each, no top-level fields.
    /// The console merges top-level keys and radio entries by name, so anything not sent keeps its
    /// stored value, including an edit made in UniFi Network after our read.
    /// </summary>
    [VendorSpecific("UniFi", "rest/device PUT merges radio_table entries by name")]
    public static string ToRequestJson(IEnumerable<RadioChannelUpdate> radios) =>
        JsonSerializer.Serialize(new Dictionary<string, object> { ["radio_table"] = radios.ToList() });
}
