using NetworkOptimizer.Web.Services.Identity;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The Threat Dashboard's CrowdSec daily-quota banner, dismissed per user and per site for the rest of
/// the UTC day. CrowdSec's quota is daily, so a quota that runs out again on a later day shows it again.
/// </summary>
public static class CtiQuotaBannerDismissal
{
    /// <summary>The preference key for one site; <see cref="IUserPreferences"/> adds the user.</summary>
    public static string KeyFor(string siteSlug) => $"threats.cti_quota_dismissed:{siteSlug}";

    /// <summary>Whether the caller dismissed the banner for the site on <paramref name="nowUtc"/>'s day.</summary>
    public static async Task<bool> IsDismissedAsync(IUserPreferences preferences, string siteSlug, DateTime nowUtc) =>
        await preferences.GetAsync(KeyFor(siteSlug)) == Day(nowUtc);

    /// <summary>Hides the banner for the caller on the site until the end of <paramref name="nowUtc"/>'s day.</summary>
    public static Task DismissAsync(IUserPreferences preferences, string siteSlug, DateTime nowUtc) =>
        preferences.SetAsync(KeyFor(siteSlug), Day(nowUtc));

    private static string Day(DateTime nowUtc) => DateOnly.FromDateTime(nowUtc).ToString("yyyy-MM-dd");
}
