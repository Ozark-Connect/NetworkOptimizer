using Microsoft.AspNetCore.Http;
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

    /// <summary>Signs the current user out of the application cookie.</summary>
    Task SignOutAsync();
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
        var isAdmin = await _userManager.IsInRoleAsync(user, GlobalRoles.Admin);
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

            // Step-up-to-enrollment: a role that requires MFA, with no second factor at all, must
            // enroll before proceeding (design doc 02 - enforced, not a banner). A passkey counts, so
            // a passkey user is not pushed into enrolling the weaker factor. Recovery boots are exempt.
            if (method != "recovery"
                && await _mfa.RoleRequiresMfaAsync(user)
                && !await _mfa.HasSecondFactorAsync(user))
            {
                return SignInOutcome.RequiresMfaEnrollment;
            }

            return SignInOutcome.Success;
        }

        if (result.RequiresTwoFactor)
            return SignInOutcome.RequiresTwoFactor;

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
    public Task SignOutAsync() => _signInManager.SignOutAsync();

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
