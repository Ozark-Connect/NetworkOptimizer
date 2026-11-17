using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetworkOptimizer.Web.Services.LanFlowMap;

/// <summary>
/// The clients one user has hidden from the 3D LAN Flow Map on one site (a management interface, a
/// device they do not want in the picture). A view choice only: a hidden client's traffic still
/// counts everywhere. Stored through <c>IUserPreferences</c> as a JSON array of MACs.
/// </summary>
public static partial class HiddenMapDevices
{
    /// <summary>More than any site hides by hand; bounds what one request can store.</summary>
    public const int MaxDevices = 1000;

    [GeneratedRegex("^[0-9a-f]{2}(:[0-9a-f]{2}){5}$")]
    private static partial Regex MacPattern();

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

    /// <summary>Lowercase colon-separated MACs, each once, in the order given. Anything else is dropped.</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string?>? macs) =>
        (macs ?? [])
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m!.Trim().Replace('-', ':').ToLowerInvariant())
            .Where(m => MacPattern().IsMatch(m))
            .Distinct()
            .Take(MaxDevices)
            .ToList();
}
