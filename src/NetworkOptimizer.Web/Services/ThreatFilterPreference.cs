using NetworkOptimizer.Web.Services.Identity;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The Threat Dashboard noise-filter toggle, remembered per user and per site.
/// </summary>
public static class ThreatFilterPreference
{
    /// <summary>The install-wide key the toggle used before it was per user. Read as the default only.</summary>
    public const string LegacyKey = "threats.filters_active";

    /// <summary>The preference key for one site; <see cref="IUserPreferences"/> adds the user.</summary>
    public static string KeyFor(string siteSlug) => $"{LegacyKey}:{siteSlug}";

    /// <summary>
    /// The caller's value for the site, else the legacy install-wide value, else on.
    /// <see cref="IUserPreferences.GetAsync"/> falls back to the per-site key, which nothing writes
    /// with authentication on, so the legacy key is read explicitly.
    /// </summary>
    public static async Task<bool> LoadAsync(
        IUserPreferences preferences, string siteSlug, Func<Task<string?>> readLegacy)
    {
        var value = await preferences.GetAsync(KeyFor(siteSlug)) ?? await readLegacy();
        return value != "false";
    }

    /// <summary>Stores the caller's value for the site.</summary>
    public static Task SaveAsync(IUserPreferences preferences, string siteSlug, bool active) =>
        preferences.SetAsync(KeyFor(siteSlug), active ? "true" : "false");
}
