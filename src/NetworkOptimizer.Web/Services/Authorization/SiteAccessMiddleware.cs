namespace NetworkOptimizer.Web.Services.Authorization;

/// <summary>
/// Pins the request's scope to a site the caller is actually allowed to see. <c>?site=</c> and the
/// site cookie are pure routing inputs - <see cref="SiteContextService.IsSelectableSite"/> checks the
/// slug alphabet and that the database exists, never membership - so without this a non-Admin could
/// read any provisioned site by URL while <c>RestrictSitesToMembers</c> was on.
///
/// Runs after the caller context is established and pins through
/// <see cref="SiteContextService.OverrideSite"/>, which the site context documents as the way to fix
/// a scope's site before any scoped service resolves its DbContext. Installs with authentication
/// disabled, background scopes, and every install with the setting off resolve to "no filtering" and
/// are left completely untouched.
/// </summary>
public sealed class SiteAccessMiddleware
{
    private readonly RequestDelegate _next;

    public SiteAccessMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ISiteAccessFilter access, SiteContextService siteContext)
    {
        var authorized = await access.AuthorizedSlugsAsync();
        if (authorized is null)
        {
            await _next(context);
            return;
        }

        var requested = siteContext.Slug;
        if (!authorized.Contains(requested))
        {
            siteContext.OverrideSite(await access.FallbackSlugAsync());

            // The cookie is this browser's default site; leaving it pointing at a site the user may
            // not see would re-select it on every later request that carries no ?site=.
            context.Response.Cookies.Delete(SiteContextService.CookieName, new CookieOptions { Path = "/" });
        }

        await _next(context);
    }
}
