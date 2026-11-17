using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetworkOptimizer.Web.Services.LanFlowMap;

/// <summary>
/// What one user has hidden from the 3D LAN Flow Map on one site: clients, by MAC (a management
/// interface, a device they do not want in the picture), and group nodes, by node id (the hub a
/// multi-client port draws; hidden, its members draw from the device upstream). A view choice
/// only: hidden traffic still counts everywhere. Stored through <c>IUserPreferences</c> as a JSON array.
/// </summary>
public static partial class HiddenMapDevices
{
    /// <summary>More than any site hides by hand; bounds what one request can store.</summary>
    public const int MaxDevices = 1000;

    // A client MAC, or a group node's id as LanFlowMapService names it: hub-dev-{parent MAC}-{port
    // key}, where the port key is {parent MAC}|{interface name}. Keep the "|": without it every
    // group id is dropped on save.
    [GeneratedRegex(@"^([0-9a-f]{2}(:[0-9a-f]{2}){5}|hub-dev-[0-9a-f]{2}(:[0-9a-f]{2}){5}-[0-9a-f]{2}(:[0-9a-f]{2}){5}\|[\w ./:-]{1,64})$")]
    private static partial Regex KeyPattern();

    /// <summary>The preference key for a site. Site-scoped: the value names that site's clients.</summary>
    public static string Key(string siteSlug) => $"lanMap3d.hiddenDevices:{siteSlug}";

    /// <summary>The stored list. Empty for nothing stored or a value that does not parse.</summary>
    public static IReadOnlyList<string> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return [];
        try
        {
            return Normalize(JsonSerializer.Deserialize<string[]>(stored));
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Lowercase colon-separated MACs and group node ids, each once, in the order given. Anything
    /// else is dropped. A group id is kept as written: its port key is case-sensitive.
    /// </summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string?>? keys) =>
        (keys ?? [])
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k!.Trim())
            .Select(k => k.StartsWith("hub-", StringComparison.Ordinal) ? k : k.Replace('-', ':').ToLowerInvariant())
            .Where(k => KeyPattern().IsMatch(k))
            .Distinct()
            .Take(MaxDevices)
            .ToList();
}
