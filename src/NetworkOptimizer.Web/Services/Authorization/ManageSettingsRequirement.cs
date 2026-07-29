using Microsoft.AspNetCore.Authorization;
using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Authorization;

/// <summary>
/// Reaching the Settings page: a global Admin anywhere, or a Site Admin on the managed site being
/// viewed. Site-scoped settings (monitoring setup, agent enrolment, that site's Identity tab) belong
/// to the site's own admin; the default site carries the instance-wide settings, so it stays Admin-only
/// (design doc 04).
/// </summary>
public sealed class ManageSettingsRequirement : IAuthorizationRequirement
{
}

/// <summary>
/// Resolves <see cref="ManageSettingsRequirement"/> against the site currently in context, so the page
/// attribute stays a single static policy while the answer follows the site the tab is pinned to.
/// </summary>
public sealed class ManageSettingsHandler : AuthorizationHandler<ManageSettingsRequirement>
{
    private readonly IAdminAuthService _adminAuth;
    private readonly IEffectiveSiteRoleResolver _resolver;
    private readonly SiteContextService _siteContext;
    private readonly ILogger<ManageSettingsHandler> _logger;

    public ManageSettingsHandler(
        IAdminAuthService adminAuth,
        IEffectiveSiteRoleResolver resolver,
        SiteContextService siteContext,
        ILogger<ManageSettingsHandler> logger)
    {
        _logger = logger;
        _adminAuth = adminAuth;
        _resolver = resolver;
        _siteContext = siteContext;
    }

    /// <inheritdoc />
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ManageSettingsRequirement requirement)
    {
        // TEMPORARY entry trace: proves whether the handler is entered at all, and for which site.
        _logger.LogDebug(
            "ManageSettings ENTER: user={User} authed={Authed}",
            context.User.Identity?.Name ?? "(anon)",
            context.User.Identity?.IsAuthenticated == true);

        // Authentication disabled for the install: the local operator does everything, as before.
        if (!await _adminAuth.IsAuthenticationRequiredAsync())
        {
            context.Succeed(requirement);
            return;
        }

        if (context.User.IsInRole(Roles.Admin))
        {
            context.Succeed(requirement);
            return;
        }

        // The default site holds the instance-wide settings, so a site role never grants it.
        if (_siteContext.IsDefault)
            return;

        string slug;
        SiteRole? effective;
        try
        {
            slug = _siteContext.Slug;
            effective = await _resolver.GetEffectiveRoleAsync(context.User, slug);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ManageSettings: resolving the site role threw");
            throw;
        }

        // TEMPORARY trace: a Site Admin of the default site is being refused Settings while the same
        // account admits fine on a managed site, and every value on the path reads correct. Says what
        // the handler actually asked and what it got back. Remove once understood.
        _logger.LogDebug(
            "ManageSettings: user={User} id={Id} slug={Slug} effective={Effective} globalAdmin={Admin}",
            context.User.Identity?.Name ?? "(anon)",
            context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "(none)",
            slug,
            effective?.ToString() ?? "(null)",
            context.User.IsInRole(Roles.Admin));

        if (effective == SiteRole.SiteAdmin)
            context.Succeed(requirement);
    }
}
