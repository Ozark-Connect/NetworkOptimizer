using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Creates, renames, enables, and deletes managed sites (and owns the multi-site toggle). Gated at
/// the service layer (design doc 06, gate 9): the site list and licence-slot reads are open to any
/// authenticated user, every change to the site registry is Admin-only and audited.
/// </summary>
[MutatingService]
public interface ISiteManagementService
{
    /// <summary>True when multi-site management is turned on for this install.</summary>
    [RequireRole(Roles.Viewer)]
    Task<bool> IsMultiSiteEnabledAsync();

    /// <summary>Turns multi-site management on or off.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Site, TargetType = "multi_site")]
    Task SetMultiSiteEnabledAsync(bool enabled);

    /// <summary>Licensed site limit for this install.</summary>
    [RequireRole(Roles.Viewer)]
    Task<int> GetSiteLimitAsync();

    /// <summary>Licensed site slots still available.</summary>
    [RequireRole(Roles.Viewer)]
    Task<int> RemainingSiteSlotsAsync();

    /// <summary>All managed sites.</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<Site>> GetSitesAsync();

    /// <summary>Persists edits to a site (name, notes, ordering).</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SiteChanged, Category = AuditCategories.Site, TargetType = "site")]
    Task UpdateSiteAsync(Site site);

    /// <summary>
    /// Renames one site. Separate from <see cref="UpdateSiteAsync"/> because it is the one edit a
    /// Site Admin owns: the label on a site they run, checked against that site rather than the
    /// instance. Everything else on a site - enabling it, deleting it, its place in the order -
    /// stays with a global Admin, so this deliberately cannot reach those fields.
    /// </summary>
    [RequireSiteRole(SiteRole.SiteAdmin)]
    [AuditAction(AuditActions.SiteChanged, Category = AuditCategories.Site, TargetType = "site")]
    Task RenameSiteAsync([SiteSlug] string siteSlug, string name);

    /// <summary>Enables or disables a site (a disabled site stops collecting and is hidden).</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SiteChanged, Category = AuditCategories.Site, TargetType = "site")]
    Task SetSiteEnabledAsync(Site site, bool enabled);

    /// <summary>Deletes a site and its database.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SiteChanged, Category = AuditCategories.Site, TargetType = "site")]
    Task DeleteSiteAsync(Site site);

    /// <summary>Slug a site with this name would get (used by the create form).</summary>
    [RequireRole(Roles.Viewer)]
    Task<string> PreviewSlugAsync(string name);

    /// <summary>Creates a managed site and its database.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SiteChanged, Category = AuditCategories.Site, TargetType = "site")]
    Task<Site> CreateSiteAsync(string name);
}
