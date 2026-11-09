using System.Text.Json.Serialization;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// The site's `locale` setting from GET get/setting/locale:
/// <c>{"_id":"…","key":"locale","site_id":"…","timezone":"America/Chicago"}</c>.
/// </summary>
public class UniFiLocaleSetting
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    /// <summary>The site's timezone, IANA form ("America/Chicago").</summary>
    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }
}
