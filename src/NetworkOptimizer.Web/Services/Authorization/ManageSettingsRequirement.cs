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

    public ManageSettingsHandler(
        IAdminAuthService adminAuth,
        IEffectiveSiteRoleResolver resolver,
        SiteContextService siteContext)
    {
        _adminAuth = adminAuth;
        _resolver = resolver;
        _siteContext = siteContext;
    }

    /// <inheritdoc />
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ManageSettingsRequirement requirement)
    {
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

        var effective = await _resolver.GetEffectiveRoleAsync(context.User, _siteContext.Slug);
        if (effective == SiteRole.SiteAdmin)
            context.Succeed(requirement);
    }
}
