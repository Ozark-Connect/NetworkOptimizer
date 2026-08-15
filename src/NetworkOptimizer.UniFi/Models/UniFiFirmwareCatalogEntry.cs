using System.Text.Json.Serialization;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// One entry of the console's firmware catalog (POST cmd/firmware {"cmd":"list-available"}):
/// the newest build for a model on the console's CURRENT channel, with a direct download URL.
/// Change the channel and re-run the command to get that channel's URLs.
/// </summary>
[VendorSpecific("UniFi", "cmd/firmware list-available entry")]
public class UniFiFirmwareCatalogEntry
{
    /// <summary>Model code the build applies to (e.g. "UP1").</summary>
    [JsonPropertyName("base_model")]
    public string? BaseModel { get; set; }

    /// <summary>Device code; usually the same as <see cref="BaseModel"/>.</summary>
    [JsonPropertyName("device")]
    public string? Device { get; set; }

    [JsonPropertyName("knownDevice")]
    [JsonConverter(typeof(FlexibleNullableBoolConverter))]
    public bool? KnownDevice { get; set; }

    [JsonPropertyName("siteDevice")]
    [JsonConverter(typeof(FlexibleNullableBoolConverter))]
    public bool? SiteDevice { get; set; }

    /// <summary>Dotted build string as the console reports it, e.g. "2.2.6.532".</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>Direct firmware image URL - the source for an SSH `upgrade &lt;url&gt;`.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("md5sum")]
    public string? Md5Sum { get; set; }

    [JsonPropertyName("bundled")]
    [JsonConverter(typeof(FlexibleNullableBoolConverter))]
    public bool? Bundled { get; set; }
}
