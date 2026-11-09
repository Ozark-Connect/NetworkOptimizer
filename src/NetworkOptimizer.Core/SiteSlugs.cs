namespace NetworkOptimizer.Core;

/// <summary>
/// Site slugs every project agrees on. Lives in Core because Alerts and Threats see only Core;
/// <c>SiteDatabasePaths.DefaultSiteSlug</c> and <c>SiteManagementService.DefaultSiteSlug</c> point here.
/// </summary>
public static class SiteSlugs
{
    /// <summary>
    /// The default site's selectable slug. Site creation reserves it, and many publishers pass the
    /// default site as null instead, so code that receives a slug treats the two alike.
    /// </summary>
    public const string Default = "main";
}
