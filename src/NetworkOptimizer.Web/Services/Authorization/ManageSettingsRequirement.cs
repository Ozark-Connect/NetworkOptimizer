using Microsoft.AspNetCore.Authorization;
using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Authorization;

/// <summary>
/// Reaching the Settings page: a global Admin anywhere, or a Site Admin on the site being viewed -
/// the default site included. Site-scoped settings (monitoring setup, agent enrolment, that site's
/// Identity tab) belong to the site's own admin, and the main site has those like any other. The
/// instance-wide cards that also live on that page carry their own Admin gates (design doc 04).
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

        // The site being viewed decides this, including the default one. It used to be refused
        // outright on the grounds that it holds the instance-wide settings - but "instance-wide" is a
        // property of particular CARDS, not of the site, and those carry their own Admin gates
        // (Admin Password, Audit Log, Multi-Site, the instance view of Identity). Refusing the whole
        // page instead meant a Site Admin of the main site could administer nothing, which for a
        // single-site install is every site they have.
        var effective = await _resolver.GetEffectiveRoleAsync(context.User, _siteContext.Slug);
        if (effective == SiteRole.SiteAdmin)
            context.Succeed(requirement);
    }
}
