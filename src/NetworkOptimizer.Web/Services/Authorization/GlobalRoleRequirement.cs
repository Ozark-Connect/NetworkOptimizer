using Microsoft.AspNetCore.Authorization;
using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Authorization;

/// <summary>
/// Requires a minimum global role (design doc 04). The roles are a strict hierarchy, so the
/// requirement is satisfied by <see cref="Minimum"/> or anything above it.
/// </summary>
public sealed class GlobalRoleRequirement : IAuthorizationRequirement
{
    public GlobalRoleRequirement(string minimum) => Minimum = minimum;

    /// <summary>Least-privileged global role that satisfies the requirement (see <see cref="Roles"/>).</summary>
    public string Minimum { get; }
}

/// <summary>
/// The single handler for global-role requirements. It is also where the "authentication is turned
/// off for this install" case is answered once: with no admin password configured there is no
/// principal to authorize and the local operator has always been able to do everything, so every
/// global-role gate succeeds. That keeps the single-admin / auth-disabled install (the overwhelmingly
/// common one) behaving exactly as it did before roles existed.
/// </summary>
public sealed class GlobalRoleHandler : AuthorizationHandler<GlobalRoleRequirement>
{
    private readonly IAdminAuthService _adminAuth;

    public GlobalRoleHandler(IAdminAuthService adminAuth) => _adminAuth = adminAuth;

    /// <inheritdoc />
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, GlobalRoleRequirement requirement)
    {
        if (!await _adminAuth.IsAuthenticationRequiredAsync())
        {
            context.Succeed(requirement);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
            return;

        // An authenticated user with no role claim is a Viewer: read access is "any authenticated".
        var rank = Roles.Rank(Roles.Viewer);
        foreach (var role in Roles.All)
        {
            if (context.User.IsInRole(role))
                rank = Math.Max(rank, Roles.Rank(role));
        }

        if (rank >= Roles.Rank(requirement.Minimum))
            context.Succeed(requirement);
    }
}
