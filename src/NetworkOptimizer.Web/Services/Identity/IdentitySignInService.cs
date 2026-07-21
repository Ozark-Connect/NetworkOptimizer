using Microsoft.AspNetCore.Identity;
using NetworkOptimizer.Storage.Models.Identity;

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

    /// <summary>Signs the current user out of the application cookie.</summary>
    Task SignOutAsync();
}

/// <inheritdoc />
public sealed class IdentitySignInService : IIdentitySignInService
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAuthPolicyOptions _policy;
    private readonly ILogger<IdentitySignInService> _logger;

    public IdentitySignInService(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        IAuthPolicyOptions policy,
        ILogger<IdentitySignInService> logger)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _policy = policy;
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
            user.LastLoginAt = DateTime.UtcNow;
            user.LastLoginMethod = BreakGlass.IsRecoveryMode && isAdmin ? "recovery" : "password";
            await _userManager.UpdateAsync(user);
            _logger.LogInformation("Local sign-in succeeded for {User} (method={Method}).", username, user.LastLoginMethod);
            return SignInOutcome.Success;
        }

        if (result.RequiresTwoFactor)
            return SignInOutcome.RequiresTwoFactor;

        if (result.IsLockedOut)
        {
            _logger.LogWarning("Local sign-in locked out for {User}.", username);
            return SignInOutcome.LockedOut;
        }

        _logger.LogInformation("Local sign-in failed for {User}: bad password.", username);
        return SignInOutcome.Failed;
    }

    /// <inheritdoc />
    public Task SignOutAsync() => _signInManager.SignOutAsync();
}
