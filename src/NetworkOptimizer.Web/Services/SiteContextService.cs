using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Scoped ambient site context. The current site is carried in the URL's ?site=
/// query parameter (per browser tab) with a cookie as the browser-wide default,
/// so it is readable synchronously during prerender, in API endpoints, and when
/// the scoped DbContext options are built - no site parameter threading anywhere.
/// Scopes without an HTTP context (background jobs, startup) resolve to the
/// default site, preserving single-site behavior exactly.
/// </summary>
public class SiteContextService : IAlertSiteScope
{
    /// <summary>
    /// Cookie carrying this browser's default site slug: the site used by any URL
    /// that has no <see cref="SiteQueryParam"/> selector (new tabs, bare bookmarks).
    /// Written only by an explicit switch in the UI, never by following a link.
    /// </summary>
    public const string CookieName = "no-site";

    /// <summary>
    /// Query-string parameter that pins a browser tab to a site. It wins over
    /// <see cref="CookieName"/> on every request, the interactive circuit pins its
    /// scope from it (<see cref="PinFromUri"/>), and the SiteTabSync component keeps
    /// it in the address bar - so different tabs can work on different sites, and
    /// alert "View" links land on their originating site without affecting other tabs.
    /// </summary>
    public const string SiteQueryParam = "site";

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

    /// <summary>
    /// True when the slug is selectable for this instance: the default site, or a
    /// non-default site whose database is provisioned. Validates against the slug
    /// alphabet and the on-disk database so a stale or tampered value can never route
    /// to an arbitrary path. Shared by cookie/query resolution and the selection
    /// middleware.
    /// </summary>
    public bool IsSelectableSite(string? slug) =>
        !string.IsNullOrEmpty(slug) &&
        (slug == SiteManagementService.DefaultSiteSlug ||
         (StringUtilities.IsSlug(slug) && File.Exists(_dbPaths.GetSiteDbPath(slug, isDefault: false))));

    /// <summary>
    /// Pins this scope to the site carried in the given URL's ?site= parameter, if
    /// present and selectable. Called by the interactive root component before any
    /// other scoped service resolves the site, so a Blazor circuit follows its own
    /// tab's URL rather than the shared browser cookie (the circuit's WebSocket
    /// upgrade request carries only the cookie).
    /// </summary>
    public void PinFromUri(string uri)
    {
        var slug = GetSiteParam(uri);
        if (slug != null && IsSelectableSite(slug))
            _slug = slug;
    }

    /// <summary>
    /// Returns the URL with its ?site= parameter set to the given slug (replacing any
    /// existing value, preserving other query parameters). Full-page navigations must
    /// stamp their target through this so a pinned tab keeps its site across the reload.
    /// </summary>
    public static string WithSiteParam(string url, string slug)
    {
        var fragment = "";
        var fragmentAt = url.IndexOf('#');
        if (fragmentAt >= 0)
        {
            fragment = url[fragmentAt..];
            url = url[..fragmentAt];
        }

        var stripped = RemoveSiteParam(url);
        var separator = stripped.Contains('?') ? '&' : '?';
        return $"{stripped}{separator}{SiteQueryParam}={Uri.EscapeDataString(slug)}{fragment}";
    }

    /// <summary>Returns the URL with any ?site= parameter removed, other parameters and fragment intact.</summary>
    public static string RemoveSiteParam(string url)
    {
        var fragment = "";
        var fragmentAt = url.IndexOf('#');
        if (fragmentAt >= 0)
        {
            fragment = url[fragmentAt..];
            url = url[..fragmentAt];
        }

        var queryAt = url.IndexOf('?');
        if (queryAt < 0)
            return url + fragment;

        var kept = url[(queryAt + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.Equals(SiteQueryParam, StringComparison.OrdinalIgnoreCase)
                     && !p.StartsWith(SiteQueryParam + "=", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var basePart = url[..queryAt];
        return (kept.Count > 0 ? $"{basePart}?{string.Join('&', kept)}" : basePart) + fragment;
    }

    private static string? GetSiteParam(string uri)
    {
        var queryAt = uri.IndexOf('?');
        if (queryAt < 0)
            return null;

        var query = uri[(queryAt + 1)..];
        var fragmentAt = query.IndexOf('#');
        if (fragmentAt >= 0)
            query = query[..fragmentAt];

        var value = query
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.StartsWith(SiteQueryParam + "=", StringComparison.OrdinalIgnoreCase))
            .Select(p => Uri.UnescapeDataString(p[(SiteQueryParam.Length + 1)..]))
            .FirstOrDefault();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private string Resolve()
    {
        var request = _httpContextAccessor.HttpContext?.Request;

        // A ?site= query param (a tab pin or an alert "View" link) wins over the cookie
        // so the page renders the correct site on first paint and API requests resolve
        // the site of the tab that issued them. An invalid value falls through.
        var queryParam = request?.Query[SiteQueryParam].ToString();
        if (!string.IsNullOrEmpty(queryParam) && IsSelectableSite(queryParam))
            return queryParam;

        var cookie = request?.Cookies[CookieName];
        if (string.IsNullOrEmpty(cookie) || cookie == SiteManagementService.DefaultSiteSlug)
            return SiteManagementService.DefaultSiteSlug;

        if (!IsSelectableSite(cookie))
            return SiteManagementService.DefaultSiteSlug;

        return cookie;
    }
}
