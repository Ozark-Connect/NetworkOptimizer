using Microsoft.AspNetCore.Authorization;

namespace NetworkOptimizer.Web.Services.Authorization;

/// <summary>
/// The single authorization handler for site-scoped requirements (design doc 04/06): it resolves the
/// user's effective role on the resource site via <see cref="IEffectiveSiteRoleResolver"/> and
/// succeeds when it meets the requirement's minimum. Every site-scoped gate (endpoints, pages, and the
/// service-layer interceptor) funnels through here, so the effective-role rule lives in exactly one place.
/// </summary>
public sealed class SiteRoleHandler : AuthorizationHandler<SiteRoleRequirement, string>
{
    private readonly IEffectiveSiteRoleResolver _resolver;
    private readonly IAdminAuthService _adminAuth;

    public SiteRoleHandler(IEffectiveSiteRoleResolver resolver, IAdminAuthService adminAuth)
    {
        _resolver = resolver;
        _adminAuth = adminAuth;
    }

    /// <inheritdoc />
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, SiteRoleRequirement requirement, string resource)
    {
        // Authentication disabled for the install: no principal to resolve, local operator does
        // everything (see GlobalRoleHandler for the same rule on global gates).
        if (!await _adminAuth.IsAuthenticationRequiredAsync())
        {
            context.Succeed(requirement);
            return;
        }

        // Enrolment is outstanding, so this session can do nothing yet - a site role is not a way
        // round that (see MfaEnrollmentGuard).
        if (context.User.IsConfinedToMfaEnrollment())
            return;

        var effective = await _resolver.GetEffectiveRoleAsync(context.User, resource);
        if (effective is not null && (int)effective.Value >= (int)requirement.Minimum)
            context.Succeed(requirement);
    }
}
