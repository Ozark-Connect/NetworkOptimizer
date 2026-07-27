using Microsoft.AspNetCore.Identity;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Auditing;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>Outcome of a local password sign-in attempt.</summary>
public enum SignInOutcome
{
    /// <summary>Authenticated; the application cookie was issued.</summary>
    Success,

    /// <summary>Wrong credentials, unknown user, or locally disabled account (reported uniformly to avoid enumeration).</summary>
    Failed,

    /// <summary>Too many failed attempts; the account is temporarily locked.</summary>
    LockedOut,

    /// <summary>Credentials were correct but a second factor is required (redirect to the 2FA step).</summary>
    RequiresTwoFactor,

    /// <summary>
    /// The password was correct, but the account's role requires a second factor and the only one it
    /// holds is a passkey - which a password sign-in never exercises. The session is refused and the
    /// user is sent back to sign in with the passkey itself.
    /// </summary>
    RequiresPasskeySignIn,

    /// <summary>Signed in, but the user's role requires MFA and none is enrolled - force step-up enrollment.</summary>
    RequiresMfaEnrollment,

    /// <summary>Local password login is disabled (SSO-only) and this attempt is not a break-glass admin login.</summary>
    LocalLoginDisabled,
}

/// <summary>
/// The single local sign-in/out funnel used by the login endpoint (design doc 06, gate 6). Wraps
/// <see cref="SignInManager{TUser}"/>, applies the local-enablement and break-glass rules, records
/// last-login metadata, and is the one place login audit events will be emitted.
/// </summary>
public interface IIdentitySignInService
{
    /// <summary>Attempts a local username/password sign-in, issuing the application cookie on success.</summary>
    Task<SignInOutcome> PasswordSignInAsync(string username, string password, bool rememberMe);

    /// <summary>Completes the second-factor step with a TOTP authenticator code.</summary>
    Task<SignInOutcome> TwoFactorSignInAsync(string code, bool rememberMe, bool rememberMachine);

    /// <summary>Completes the second-factor step with a single-use recovery code.</summary>
    Task<SignInOutcome> RecoveryCodeSignInAsync(string recoveryCode);

    /// <summary>
    /// Completes a passkey assertion the browser already proved. Routed through here rather than
    /// signing in at the endpoint so a passkey login gets the same treatment as a password one:
    /// the local enablement gate, last-login metadata, and a login audit event.
    /// </summary>
    Task<SignInOutcome> PasskeySignInAsync(ApplicationUser user, bool rememberMe);

    /// <summary>Signs the current user out of the application cookie.</summary>
    Task SignOutAsync();

    /// <summary>
    /// Re-issues the application cookie for the signed-in user. Enabling or disabling a second factor
    /// rotates the security stamp, which leaves the existing cookie stale - the live circuit then
    /// revalidates as signed-out and the app starts failing until the user signs in again. Only an
    /// HTTP request can write the replacement cookie, so this is called from an endpoint.
    /// </summary>
    Task RefreshSignInAsync(ApplicationUser user);
}

/// <inheritdoc />
public sealed class IdentitySignInService : IIdentitySignInService
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAuthPolicyOptions _policy;
    private readonly IMfaService _mfa;
    private readonly IAuditLogger _audit;
    private readonly IHttpContextAccessor _httpContext;
    private readonly ILogger<IdentitySignInService> _logger;

    public IdentitySignInService(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        IAuthPolicyOptions policy,
        IMfaService mfa,
        IAuditLogger audit,
        IHttpContextAccessor httpContext,
        ILogger<IdentitySignInService> logger)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _policy = policy;
        _mfa = mfa;
        _audit = audit;
        _httpContext = httpContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SignInOutcome> PasswordSignInAsync(string username, string password, bool rememberMe)
    {
        var user = await _userManager.FindByNameAsync(username);

        // Uniform failure for unknown or disabled accounts - do not distinguish (no enumeration).
        if (user is null || !user.IsEnabled)
        {
            _logger.LogInformation("Local sign-in failed for {User}: unknown or disabled account.", username);
            EmitLogin(username, user?.Id, AuditActions.LoginFailed, AuditOutcomes.Failure, "password");
            return SignInOutcome.Failed;
        }

        // SSO-only installs disable local password login, except a break-glass admin recovery boot.
        var isAdmin = await _userManager.IsInRoleAsync(user, Roles.Admin);
        if (await _policy.IsLocalLoginDisabledAsync() && !(BreakGlass.IsRecoveryMode && isAdmin))
        {
            _logger.LogWarning("Local sign-in blocked for {User}: local login is disabled (SSO-only).", username);
            return SignInOutcome.LocalLoginDisabled;
        }

        var result = await _signInManager.PasswordSignInAsync(user, password, rememberMe, lockoutOnFailure: true);

        if (result.Succeeded)
        {
            var method = BreakGlass.IsRecoveryMode && isAdmin ? "recovery" : "password";
            user.LastLoginAt = DateTime.UtcNow;
            user.LastLoginMethod = method;
            await _userManager.UpdateAsync(user);
            _logger.LogInformation("Local sign-in succeeded for {User} (method={Method}).", username, method);
            EmitLogin(username, user.Id,
                method == "recovery" ? AuditActions.BreakGlassUsed : AuditActions.LoginSuccess,
                AuditOutcomes.Success, method);

            // A role that requires a second factor. Recovery boots are exempt.
            if (method != "recovery" && await _mfa.RoleRequiresMfaAsync(user))
            {
                // Nothing enrolled at all: step up to enrollment (design doc 02 - enforced, not a banner).
                if (!await _mfa.HasSecondFactorAsync(user))
                    return SignInOutcome.RequiresMfaEnrollment;

                // A passkey satisfies the requirement, but only when it is the credential actually
                // used. Identity treats a passkey as primary passwordless auth, not as a challenge
                // after a password, so a password sign-in here would be single-factor despite the
                // policy. Refuse the session and send them back to use the passkey.
                if (!await _mfa.IsEnabledAsync(user))
                {
                    await _signInManager.SignOutAsync();
                    _logger.LogInformation(
                        "Local sign-in for {User} refused: role requires a second factor and only a passkey is enrolled.",
                        username);
                    return SignInOutcome.RequiresPasskeySignIn;
                }
            }

            return SignInOutcome.Success;
        }

        if (result.RequiresTwoFactor)
        {
            // Logged because it is otherwise the one sign-in outcome that leaves no trace: the
            // password was right and no session was issued yet, so a problem in the second-factor
            // step looks like nothing happened at all.
            _logger.LogInformation("Local sign-in for {User} requires a second factor.", username);
            return SignInOutcome.RequiresTwoFactor;
        }

        if (result.IsLockedOut)
        {
            _logger.LogWarning("Local sign-in locked out for {User}.", username);
            EmitLogin(username, user.Id, AuditActions.Lockout, AuditOutcomes.Failure, "password");
            return SignInOutcome.LockedOut;
        }

        _logger.LogInformation("Local sign-in failed for {User}: bad password.", username);
        EmitLogin(username, user.Id, AuditActions.LoginFailed, AuditOutcomes.Failure, "password");
        return SignInOutcome.Failed;
    }

    /// <inheritdoc />
    public async Task<SignInOutcome> TwoFactorSignInAsync(string code, bool rememberMe, bool rememberMachine)
    {
        var normalized = code.Replace(" ", string.Empty).Replace("-", string.Empty);
        var result = await _signInManager.TwoFactorAuthenticatorSignInAsync(normalized, rememberMe, rememberMachine);
        var user = await _signInManager.GetTwoFactorAuthenticationUserAsync();
        if (result.Succeeded)
        {
            if (user is not null)
                EmitLogin(user.UserName ?? "", user.Id, AuditActions.LoginSuccess, AuditOutcomes.Success, "totp");
            return SignInOutcome.Success;
        }
        if (result.IsLockedOut)
            return SignInOutcome.LockedOut;
        if (user is not null)
            EmitLogin(user.UserName ?? "", user.Id, AuditActions.LoginFailed, AuditOutcomes.Failure, "totp");
        return SignInOutcome.Failed;
    }

    /// <inheritdoc />
    public async Task<SignInOutcome> RecoveryCodeSignInAsync(string recoveryCode)
    {
        var normalized = recoveryCode.Replace(" ", string.Empty).Replace("-", string.Empty);
        var result = await _signInManager.TwoFactorRecoveryCodeSignInAsync(normalized);
        var user = await _signInManager.GetTwoFactorAuthenticationUserAsync();
        if (result.Succeeded)
        {
            if (user is not null)
                EmitLogin(user.UserName ?? "", user.Id, AuditActions.RecoveryCodeUsed, AuditOutcomes.Success, "recovery_code");
            return SignInOutcome.Success;
        }
        if (result.IsLockedOut)
            return SignInOutcome.LockedOut;
        return SignInOutcome.Failed;
    }

    /// <inheritdoc />
    /// <inheritdoc />
    public async Task<SignInOutcome> PasskeySignInAsync(ApplicationUser user, bool rememberMe)
    {
        // A disabled account must not get in on a credential registered before it was disabled.
        if (!user.IsEnabled)
        {
            _logger.LogWarning("Passkey sign-in refused for {User}: the account is disabled.", user.UserName);
            EmitLogin(user.UserName ?? "", user.Id, AuditActions.LoginFailed, AuditOutcomes.Failure, "passkey");
            return SignInOutcome.Failed;
        }

        await _signInManager.SignInAsync(user, rememberMe, "passkey");

        user.LastLoginAt = DateTime.UtcNow;
        user.LastLoginMethod = "passkey";
        await _userManager.UpdateAsync(user);

        _logger.LogInformation("Passkey sign-in succeeded for {User}.", user.UserName);
        EmitLogin(user.UserName ?? "", user.Id, AuditActions.LoginSuccess, AuditOutcomes.Success, "passkey");
        return SignInOutcome.Success;
    }

    public Task SignOutAsync() => _signInManager.SignOutAsync();

    /// <inheritdoc />
    public Task RefreshSignInAsync(ApplicationUser user) => _signInManager.RefreshSignInAsync(user);

    /// <summary>Emits a login-related audit event, attributing the attempt to the supplied username + request metadata.</summary>
    private void EmitLogin(string username, string? userId, string action, string outcome, string method)
    {
        var http = _httpContext.HttpContext;
        var caller = new CallerInfo
        {
            UserId = userId,
            ActorName = username,
            AuthMethod = method,
            SourceIp = http?.Connection.RemoteIpAddress?.ToString(),
            UserAgent = http?.Request.Headers.UserAgent.ToString(),
            CorrelationId = http?.TraceIdentifier,
        };
        _audit.Log(AuditEventBuilder.From(caller, AuditCategories.Auth, action, outcome,
            targetType: "user", targetId: userId, targetName: username));
    }
}
