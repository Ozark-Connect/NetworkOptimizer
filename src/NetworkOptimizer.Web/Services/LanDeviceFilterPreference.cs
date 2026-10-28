using NetworkOptimizer.Web.Services.Identity;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The UniFi Device Speed Test family/model filter and Online only toggle, remembered per user and per site.
/// </summary>
public static class LanDeviceFilterPreference
{
    /// <summary>The preference key for the family/model filter on one site; <see cref="IUserPreferences"/> adds the user.</summary>
    public static string FamilyKeyFor(string siteSlug) => $"lan_speedtest.device_family:{siteSlug}";

    /// <summary>The preference key for Online only on one site; <see cref="IUserPreferences"/> adds the user.</summary>
    public static string OnlineOnlyKeyFor(string siteSlug) => $"lan_speedtest.online_only:{siteSlug}";

    /// <summary>The caller's filter for the site: no family (All models) and Online only off by default.</summary>
    public static async Task<(string Family, bool OnlineOnly)> LoadAsync(IUserPreferences preferences, string siteSlug)
    {
        var family = await preferences.GetAsync(FamilyKeyFor(siteSlug)) ?? "";
        var onlineOnly = await preferences.GetAsync(OnlineOnlyKeyFor(siteSlug)) == "true";
        return (family, onlineOnly);
    }

    /// <summary>Stores the caller's family/model filter for the site. Empty means All models.</summary>
    public static Task SaveFamilyAsync(IUserPreferences preferences, string siteSlug, string family) =>
        preferences.SetAsync(FamilyKeyFor(siteSlug), family);

    /// <summary>Stores the caller's Online only toggle for the site.</summary>
    public static Task SaveOnlineOnlyAsync(IUserPreferences preferences, string siteSlug, bool onlineOnly) =>
        preferences.SetAsync(OnlineOnlyKeyFor(siteSlug), onlineOnly ? "true" : "false");
}
