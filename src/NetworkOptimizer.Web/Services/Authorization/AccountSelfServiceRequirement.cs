using Microsoft.AspNetCore.Authorization;
using NetworkOptimizer.Web.Services.Identity;

namespace NetworkOptimizer.Web.Services.Authorization;

/// <summary>
/// Being signed in, and nothing more. The surface a user reaches to manage their OWN account:
/// enrolling a second factor, registering a passkey, changing their own password.
///
/// It exists because <see cref="GlobalRoleRequirement"/> refuses a session that still owes the
/// install a second factor, and enrolling one needs a session - so the account security page and the
/// enrolment endpoint cannot sit behind the same gate as the rest of the app or the requirement would
/// have no way to be satisfied. Everything reachable under this policy must act on the caller's own
/// account only; the services behind it enforce that themselves (design doc 06, gate 10).
/// </summary>
public sealed class AccountSelfServiceRequirement : IAuthorizationRequirement
{
}

/// <inheritdoc cref="AccountSelfServiceRequirement" />
public sealed class AccountSelfServiceHandler : AuthorizationHandler<AccountSelfServiceRequirement>
{
    private readonly IAdminAuthService _adminAuth;

    public AccountSelfServiceHandler(IAdminAuthService adminAuth) => _adminAuth = adminAuth;

    /// <inheritdoc />
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, AccountSelfServiceRequirement requirement)
    {
        // Same answer GlobalRoleHandler gives first: with no admin password there is no principal to
        // authorize and the local operator has always been able to do everything.
        if (!await _adminAuth.IsAuthenticationRequiredAsync())
        {
            context.Succeed(requirement);
            return;
        }

        // Deliberately does NOT consult NetOptClaims.MfaSetupPending - admitting that state is the
        // entire reason this policy exists. It also does not check site access: an account with no
        // sites still owns its own credentials.
        if (context.User.Identity?.IsAuthenticated == true)
            context.Succeed(requirement);
    }
}
