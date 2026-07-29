using System.Security.Claims;
using NetworkOptimizer.Web.Services.Identity;

namespace NetworkOptimizer.Web.Services.Authorization;

/// <summary>
/// Answers "may the current caller see this site at all", for navigation and for the site lists in
/// the UI. This is the read-side companion to <see cref="SiteRoleHandler"/>, which only refuses
/// <em>actions</em>: without it, <c>RestrictSitesToMembers</c> narrows what a non-Admin can change
/// but not what they can open with <c>?site=</c>.
/// </summary>
public interface ISiteAccessFilter
{
    /// <summary>
    /// The slugs the caller may see, or null when no filtering applies at all - authentication is
    /// disabled for the install, the call is background/system work, or no caller was established.
    /// Null means "everything", and is the answer for the single-admin install, so this never
    /// narrows anything on the installs that have no concept of membership.
    /// </summary>
    Task<IReadOnlySet<string>?> AuthorizedSlugsAsync();

    /// <summary>True when the caller may see the given site (also true whenever filtering is off).</summary>
    Task<bool> IsAuthorizedAsync(string? slug);

    /// <summary>
    /// Narrows a list of sites to the ones the caller may see, preserving order. Returned unchanged
    /// when no filtering applies, so single-site and auth-disabled installs list exactly what they
    /// always did.
    /// </summary>
    Task<List<T>> FilterAsync<T>(IEnumerable<T> sites, Func<T, string> slugSelector);

    /// <summary>
    /// The site to fall back to when the requested one is not allowed: the caller's first authorized
    /// site, or the default site when they have none (which then fails closed at the page policies
    /// rather than exposing another site's data).
    /// </summary>
    Task<string> FallbackSlugAsync();
}

/// <inheritdoc />
public sealed class SiteAccessFilter : ISiteAccessFilter
{
    private readonly ICallerContext _caller;
    private readonly IEffectiveSiteRoleResolver _resolver;

    public SiteAccessFilter(ICallerContext caller, IEffectiveSiteRoleResolver resolver)
    {
        _caller = caller;
        _resolver = resolver;
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>?> AuthorizedSlugsAsync()
    {
        // No caller means background work with no principal to filter by; system scopes and
        // auth-disabled installs have always seen every site and must keep doing so.
        var caller = _caller.Current;
        if (caller is null || caller.IsSystem || caller.AuthenticationDisabled || caller.Principal is null)
            return null;

        return await _resolver.GetAuthorizedSlugsAsync(caller.Principal);
    }

    /// <inheritdoc />
    public async Task<bool> IsAuthorizedAsync(string? slug)
    {
        if (string.IsNullOrEmpty(slug))
            return true;

        var authorized = await AuthorizedSlugsAsync();
        return authorized is null || authorized.Contains(slug);
    }

    /// <inheritdoc />
    public async Task<List<T>> FilterAsync<T>(IEnumerable<T> sites, Func<T, string> slugSelector)
    {
        var authorized = await AuthorizedSlugsAsync();
        return authorized is null
            ? sites.ToList()
            : sites.Where(s => authorized.Contains(slugSelector(s))).ToList();
    }

    /// <inheritdoc />
    public async Task<string> FallbackSlugAsync()
    {
        var authorized = await AuthorizedSlugsAsync();
        if (authorized is null || authorized.Contains(SiteManagementService.DefaultSiteSlug))
            return SiteManagementService.DefaultSiteSlug;

        return authorized.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            ?? SiteManagementService.DefaultSiteSlug;
    }
}
