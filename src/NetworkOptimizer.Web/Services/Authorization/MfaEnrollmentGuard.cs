using System.Security.Claims;
using NetworkOptimizer.Web.Services.Identity;

namespace NetworkOptimizer.Web.Services.Authorization;

/// <summary>
/// One reading of "this session is confined to MFA enrolment", for every gate that has to honour it.
///
/// A role that demands a second factor can be granted to an account that has not enrolled - at sign-in
/// or, just as easily, to a session already open. Either way the account ends up holding a cookie it
/// must not be able to use, because enrolling needs a session and there is nowhere else to do it from.
/// <see cref="NetOptClaims.MfaSetupPending"/> is what marks that state, and refusing while it is
/// present is the whole of the enforcement: the redirect into the security page is guidance, and
/// guidance is not enforcement to anything that ignores redirects.
///
/// It started life inline in <see cref="GlobalRoleHandler"/>, which left the site-scoped page gates and
/// the service-layer interceptor admitting the very sessions the global gates were turning away - a
/// confined session could still act through a site role. A claim that means "nothing works yet" has to
/// be asked about everywhere, so it lives here and every gate calls it.
///
/// <see cref="AccountSelfServiceRequirement"/> deliberately does NOT call this: admitting the state is
/// the point of that policy, and it is the one way out.
/// </summary>
public static class MfaEnrollmentGuard
{
    /// <summary>
    /// True when the principal must finish enrolling a second factor before anything else works.
    /// </summary>
    public static bool IsConfinedToMfaEnrollment(this ClaimsPrincipal? user)
        => user?.HasClaim(c => c.Type == NetOptClaims.MfaSetupPending) == true;
}
