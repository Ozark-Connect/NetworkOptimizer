using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Adds Network Optimizer's custom claims to the principal at sign-in: the membership version stamp
/// (for cache/circuit invalidation, design doc 04), the display name, and the must-enrol marker when
/// a role demands a second factor the user has not set up. Global roles are added by the base factory
/// as role claims; site memberships are intentionally resolved server-side per check rather than
/// embedded as claims.
/// </summary>
public sealed class AppUserClaimsPrincipalFactory
    : UserClaimsPrincipalFactory<ApplicationUser, ApplicationRole>
{
    private readonly MfaRequirementFacts _mfaFacts;

    public AppUserClaimsPrincipalFactory(
        UserManager<ApplicationUser> userManager,
        RoleManager<ApplicationRole> roleManager,
        IOptions<IdentityOptions> options,
        MfaRequirementFacts mfaFacts)
        : base(userManager, roleManager, options)
    {
        _mfaFacts = mfaFacts;
    }

    /// <inheritdoc />
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(NetOptClaims.MembershipVersion, user.MembershipVersion.ToString()));

        // New on every principal, which means new on every cookie: a refresh issues a different one,
        // so the session that just refreshed can be told apart from the sessions that did not.
        identity.AddClaim(new Claim(NetOptClaims.SessionId, Guid.NewGuid().ToString("N")));
        if (!string.IsNullOrEmpty(user.DisplayName))
            identity.AddClaim(new Claim(ClaimTypes.GivenName, user.DisplayName));

        // Derived here rather than stamped by the sign-in that noticed it, so refreshing the cookie
        // recomputes it instead of dropping it - otherwise any signed-in caller could clear the
        // marker by asking for a new cookie, which is the whole session-refresh endpoint. It also
        // means enrolling removes it by itself: the refresh that follows enrolment rebuilds the
        // principal and the claim is simply no longer true.
        if (await _mfaFacts.MustEnrolAsync(user))
            identity.AddClaim(new Claim(NetOptClaims.MfaSetupPending, "1"));

        return identity;
    }
}
