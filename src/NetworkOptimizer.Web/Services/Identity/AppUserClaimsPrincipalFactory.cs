using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Adds Network Optimizer's custom claims to the principal at sign-in: the membership version stamp
/// (for cache/circuit invalidation, design doc 04) and the display name. Global roles are added by
/// the base factory as role claims; site memberships are intentionally resolved server-side per check
/// rather than embedded as claims.
/// </summary>
public sealed class AppUserClaimsPrincipalFactory
    : UserClaimsPrincipalFactory<ApplicationUser, ApplicationRole>
{
    public AppUserClaimsPrincipalFactory(
        UserManager<ApplicationUser> userManager,
        RoleManager<ApplicationRole> roleManager,
        IOptions<IdentityOptions> options)
        : base(userManager, roleManager, options)
    {
    }

    /// <inheritdoc />
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(NetOptClaims.MembershipVersion, user.MembershipVersion.ToString()));
        if (!string.IsNullOrEmpty(user.DisplayName))
            identity.AddClaim(new Claim(ClaimTypes.GivenName, user.DisplayName));
        return identity;
    }
}
