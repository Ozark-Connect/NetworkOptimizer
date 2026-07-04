using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Scoped ambient site context. The current site is carried in a browser cookie
/// so it is readable synchronously during prerender, in API endpoints, and when
/// the scoped DbContext options are built - no site parameter threading anywhere.
/// Scopes without an HTTP context (background jobs, startup) resolve to the
/// default site, preserving single-site behavior exactly.
/// </summary>
public class SiteContextService : IAlertSiteScope
{
    /// <summary>Cookie carrying the selected site slug for this browser.</summary>
    public const string CookieName = "no-site";

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly SiteDatabasePaths _dbPaths;
    private string? _slug;

    public SiteContextService(IHttpContextAccessor httpContextAccessor, SiteDatabasePaths dbPaths)
    {
        _httpContextAccessor = httpContextAccessor;
        _dbPaths = dbPaths;
    }

    /// <summary>Slug of the site this scope operates on. Resolved once per scope.</summary>
    public string Slug => _slug ??= Resolve();

    /// <summary>
    /// Pins this scope to an explicit site, for scopes created outside an HTTP
    /// request (per-site background fan-out, site-bound singletons). Must be
    /// called before any scoped service resolves the DbContext.
    /// </summary>
    public void OverrideSite(string slug) => _slug = slug;

    /// <summary>
    /// <see cref="IAlertSiteScope"/>: pin this scope to the site an alert originated
    /// from (null/empty = default site) before the alert repository resolves its DbContext.
    /// </summary>
    public void UseSite(string? siteSlug) =>
        OverrideSite(string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug);

    /// <summary>True when this scope operates on the default site (the main database).</summary>
    public bool IsDefault => Slug == SiteManagementService.DefaultSiteSlug;

    /// <summary>SQLite database path for the current site.</summary>
    public string DbPath => _dbPaths.GetSiteDbPath(Slug, IsDefault);

    private string Resolve()
    {
        var cookie = _httpContextAccessor.HttpContext?.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(cookie) || cookie == SiteManagementService.DefaultSiteSlug)
            return SiteManagementService.DefaultSiteSlug;

        // Validate against the slug alphabet and require a provisioned database so a
        // stale or tampered cookie can never route to an arbitrary path.
        if (!StringUtilities.IsSlug(cookie) || !File.Exists(_dbPaths.GetSiteDbPath(cookie, isDefault: false)))
            return SiteManagementService.DefaultSiteSlug;

        return cookie;
    }
}
