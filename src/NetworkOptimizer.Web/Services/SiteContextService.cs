using System.Text.RegularExpressions;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Scoped ambient site context. The current site is carried in a browser cookie
/// so it is readable synchronously during prerender, in API endpoints, and when
/// the scoped DbContext options are built - no site parameter threading anywhere.
/// Scopes without an HTTP context (background jobs, startup) resolve to the
/// default site, preserving single-site behavior exactly.
/// </summary>
public partial class SiteContextService
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
        if (!SlugPattern().IsMatch(cookie) || !File.Exists(_dbPaths.GetSiteDbPath(cookie, isDefault: false)))
            return SiteManagementService.DefaultSiteSlug;

        return cookie;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex SlugPattern();
}
