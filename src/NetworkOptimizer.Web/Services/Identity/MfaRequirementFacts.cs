using Microsoft.AspNetCore.Identity;
using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// The two facts that decide whether a user still owes the install a second factor: does any role
/// they hold demand one, and have they enrolled anything at all.
///
/// Split out of <see cref="MfaService"/> so <see cref="AppUserClaimsPrincipalFactory"/> can ask the
/// same question without taking a dependency on it - MfaService needs
/// <see cref="SignInManager{TUser}"/>, which needs the claims factory, so injecting it there would
/// close a DI cycle. This type deliberately depends on the two managers and nothing else.
/// MfaService delegates to it, so there is one implementation of each predicate.
/// </summary>
public sealed class MfaRequirementFacts
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<ApplicationRole> _roleManager;

    public MfaRequirementFacts(
        UserManager<ApplicationUser> userManager,
        RoleManager<ApplicationRole> roleManager)
    {
        _userManager = userManager;
        _roleManager = roleManager;
    }

    /// <summary>
    /// A second factor of any kind is enrolled. A passkey counts: it satisfies a role's MFA
    /// requirement on its own, and demanding TOTP from someone who already has one would insist on
    /// the weaker factor.
    /// </summary>
    public async Task<bool> HasSecondFactorAsync(ApplicationUser user)
        => await _userManager.GetTwoFactorEnabledAsync(user)
            || (await _userManager.GetPasskeysAsync(user)).Count > 0;

    /// <summary>Any of the user's global roles has <see cref="ApplicationRole.RequireMfa"/> set.</summary>
    public async Task<bool> RoleRequiresMfaAsync(ApplicationUser user)
        => await AnyRoleRequiresMfaAsync(await _userManager.GetRolesAsync(user));

    /// <summary>The same question asked of a role set rather than of a stored user.</summary>
    public async Task<bool> AnyRoleRequiresMfaAsync(IEnumerable<string> roleNames)
    {
        foreach (var roleName in roleNames)
        {
            var appRole = await _roleManager.FindByNameAsync(roleName);
            if (appRole?.RequireMfa == true)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The user must enrol before they may use the app: a role demands a second factor and they have
    /// none. This is what <see cref="NetOptClaims.MfaSetupPending"/> is stamped from, recomputed every
    /// time a principal is built rather than recorded at sign-in - so it cannot be washed off by
    /// refreshing the cookie, and it clears itself the moment enrolment completes.
    /// </summary>
    public async Task<bool> MustEnrolAsync(ApplicationUser user)
        => await RoleRequiresMfaAsync(user) && !await HasSecondFactorAsync(user);
}
