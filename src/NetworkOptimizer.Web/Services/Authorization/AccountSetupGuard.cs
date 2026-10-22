using System.Security.Claims;
using NetworkOptimizer.Web.Services.Identity;

namespace NetworkOptimizer.Web.Services.Authorization;

/// <summary>
/// One reading of "this session is confined to finishing account setup", for every gate that has to
/// honour it.
///
/// Two states confine a session. A role that demands a second factor can be granted to an account that
/// has not enrolled (<see cref="NetOptClaims.MfaSetupPending"/>), and an administrator can set the
/// password an account signs in with (<see cref="NetOptClaims.PasswordChangePending"/>). Either way the
/// account holds a cookie it must not be able to use, because fixing it needs a session and there is
/// nowhere else to do it from. Refusing while either claim is present is the whole of the enforcement:
/// the redirect into the security page is guidance, and guidance is not enforcement to anything that
/// ignores redirects.
///
/// It started life inline in <see cref="GlobalRoleHandler"/>, which left the site-scoped page gates and
/// the service-layer interceptor admitting the very sessions the global gates were turning away - a
/// confined session could still act through a site role. A claim that means "nothing works yet" has to
/// be asked about everywhere, so it lives here and every gate calls it.
///
/// <see cref="AccountSelfServiceRequirement"/> deliberately does NOT call this: admitting the state is
/// the point of that policy, and it is the one way out.
/// </summary>
public static class AccountSetupGuard
{
    /// <summary>
    /// True when the principal must enrol a second factor or replace an administrator-set password
    /// before anything else works.
    /// </summary>
    public static bool IsConfinedToAccountSetup(this ClaimsPrincipal? user)
        => user?.HasClaim(c => c.Type is NetOptClaims.MfaSetupPending or NetOptClaims.PasswordChangePending) == true;

    /// <summary>True when the confinement includes an outstanding second factor.</summary>
    public static bool OwesSecondFactor(this ClaimsPrincipal? user)
        => user?.HasClaim(c => c.Type == NetOptClaims.MfaSetupPending) == true;
}
