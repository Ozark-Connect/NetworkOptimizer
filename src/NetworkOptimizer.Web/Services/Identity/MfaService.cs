using System.Text;
using Microsoft.AspNetCore.Identity;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Auditing;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>TOTP authenticator enrollment payload: the shared key (formatted + raw) and the otpauth URI for a QR code.</summary>
public sealed record AuthenticatorSetup(string SharedKey, string FormattedKey, string OtpAuthUri);

/// <summary>
/// Multi-factor authentication over ASP.NET Core Identity's authenticator (TOTP, RFC 6238) and
/// recovery codes (design doc 02). Per-role "Require MFA" is evaluated here; enforcement at login is
/// step-up-to-enrollment, driven by the sign-in flow and a light navigation guard.
/// </summary>
public interface IMfaService
{
    /// <summary>True when an authenticator app (TOTP) is enrolled. Specific to TOTP, so the account
    /// page can describe that factor honestly; use <see cref="HasSecondFactorAsync"/> to decide whether
    /// a role's MFA requirement is met.</summary>
    Task<bool> IsEnabledAsync(ApplicationUser user);

    /// <summary>
    /// True when the user holds any second factor: an authenticator app, or a passkey. A passkey is
    /// origin-bound and therefore phishing-resistant where a typed code is not, so it satisfies a
    /// role's MFA requirement on its own - demanding TOTP from someone who already has a passkey would
    /// insist on the weaker factor.
    /// </summary>
    Task<bool> HasSecondFactorAsync(ApplicationUser user);

    /// <summary>Any of the user's global roles has <see cref="ApplicationRole.RequireMfa"/> set.</summary>
    Task<bool> RoleRequiresMfaAsync(ApplicationUser user);

    /// <summary>Begins TOTP enrollment: resets/returns the authenticator key and the otpauth URI for the QR.</summary>
    Task<AuthenticatorSetup> BeginEnrollmentAsync(ApplicationUser user);

    /// <summary>Verifies the first TOTP code and enables two-factor for the user.</summary>
    Task<bool> CompleteEnrollmentAsync(ApplicationUser user, string code);

    /// <summary>Generates a fresh set of 10 single-use recovery codes (invalidating any prior set).</summary>
    Task<IReadOnlyList<string>> GenerateRecoveryCodesAsync(ApplicationUser user);

    /// <summary>How many unused recovery codes the user holds.</summary>
    Task<int> CountRecoveryCodesAsync(ApplicationUser user);

    /// <summary>
    /// Whether the named account holds any recovery codes. Takes a username rather than reading the
    /// pending two-factor cookie, because the sign-in that triggers the second-factor step writes
    /// that cookie into the response - it is not in the request yet, so it cannot be read back.
    /// </summary>
    Task<bool> HasRecoveryCodesAsync(string userName);

    /// <summary>
    /// True when the named account has a passkey registered. Resolved by username for the same reason
    /// as the recovery-code check: the second-factor page cannot read the pending-2FA cookie itself.
    /// </summary>
    Task<bool> HasPasskeysAsync(string userName);

    /// <summary>
    /// True when the account signs in with a local password. False for a passkey-only or
    /// federated-only account, which has nothing to change.
    /// </summary>
    Task<bool> HasPasswordAsync(ApplicationUser user);

    /// <summary>
    /// The user waiting on the second-factor step, or null when no such sign-in is in progress. Lets
    /// the 2FA page tailor itself (for example, hiding recovery-code entry for an account that has none).
    /// </summary>
    Task<ApplicationUser?> GetPendingTwoFactorUserAsync();

    /// <summary>Disables MFA and clears the authenticator key.</summary>
    /// <summary>Turns the authenticator off. False when the write was refused and MFA is still on.</summary>
    Task<bool> DisableAsync(ApplicationUser user);
}

/// <inheritdoc />
public sealed class MfaService : IMfaService
{
    private const string Issuer = "Network Optimizer";
    private const int RecoveryCodeCount = 10;

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IAuditLogger _audit;
    private readonly ICallerContext _caller;

    public MfaService(
        UserManager<ApplicationUser> userManager,
        RoleManager<ApplicationRole> roleManager,
        SignInManager<ApplicationUser> signInManager,
        IAuditLogger audit,
        ICallerContext caller)
    {
        _userManager = userManager;
        _roleManager = roleManager;
        _signInManager = signInManager;
        _audit = audit;
        _caller = caller;
    }

    public Task<bool> IsEnabledAsync(ApplicationUser user) => _userManager.GetTwoFactorEnabledAsync(user);

    /// <inheritdoc />
    public async Task<bool> HasSecondFactorAsync(ApplicationUser user)
        => await _userManager.GetTwoFactorEnabledAsync(user)
            || (await _userManager.GetPasskeysAsync(user)).Count > 0;

    public async Task<bool> RoleRequiresMfaAsync(ApplicationUser user)
    {
        var roles = await _userManager.GetRolesAsync(user);
        foreach (var roleName in roles)
        {
            var appRole = await _roleManager.FindByNameAsync(roleName);
            if (appRole?.RequireMfa == true)
                return true;
        }
        return false;
    }

    public async Task<AuthenticatorSetup> BeginEnrollmentAsync(ApplicationUser user)
    {
        var key = await _userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await _userManager.ResetAuthenticatorKeyAsync(user);
            key = await _userManager.GetAuthenticatorKeyAsync(user);
        }

        var account = user.UserName ?? user.Id;
        var uri =
            $"otpauth://totp/{Uri.EscapeDataString(Issuer)}:{Uri.EscapeDataString(account)}" +
            $"?secret={key}&issuer={Uri.EscapeDataString(Issuer)}&digits=6";
        return new AuthenticatorSetup(key!, FormatKey(key!), uri);
    }

    public async Task<bool> CompleteEnrollmentAsync(ApplicationUser user, string code)
    {
        var normalized = code.Replace(" ", string.Empty).Replace("-", string.Empty);
        var valid = await _userManager.VerifyTwoFactorTokenAsync(
            user, _userManager.Options.Tokens.AuthenticatorTokenProvider, normalized);
        if (!valid)
            return false;

        // A dropped result here would report an enrolment that never happened, which is the worst
        // direction for this to fail in - the user puts the app away believing they have a second
        // factor. See IdentityAdminService.LoadForUpdateAsync for how the write comes to fail.
        if (!(await _userManager.SetTwoFactorEnabledAsync(user, true)).Succeeded)
            return false;

        Emit(AuditActions.MfaEnrolled, user);
        return true;
    }

    /// <inheritdoc />
    public Task<int> CountRecoveryCodesAsync(ApplicationUser user)
        => _userManager.CountRecoveryCodesAsync(user);

    /// <inheritdoc />
    public Task<bool> HasPasswordAsync(ApplicationUser user) => _userManager.HasPasswordAsync(user);

    public async Task<bool> HasPasskeysAsync(string userName)
    {
        var user = await _userManager.FindByNameAsync(userName);
        return user is not null && (await _userManager.GetPasskeysAsync(user)).Count > 0;
    }

    public async Task<bool> HasRecoveryCodesAsync(string userName)
    {
        var user = await _userManager.FindByNameAsync(userName);
        return user is not null && await _userManager.CountRecoveryCodesAsync(user) > 0;
    }

    /// <inheritdoc />
    public Task<ApplicationUser?> GetPendingTwoFactorUserAsync()
        => _signInManager.GetTwoFactorAuthenticationUserAsync()!;

    public async Task<IReadOnlyList<string>> GenerateRecoveryCodesAsync(ApplicationUser user)
    {
        var codes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount);
        Emit(AuditActions.RecoveryCodesRegenerated, user);
        return codes?.ToList() ?? new List<string>();
    }

    public async Task<bool> DisableAsync(ApplicationUser user)
    {
        if (!(await _userManager.SetTwoFactorEnabledAsync(user, false)).Succeeded)
            return false;

        await _userManager.ResetAuthenticatorKeyAsync(user);
        Emit(AuditActions.MfaRemoved, user);
        return true;
    }

    private void Emit(string action, ApplicationUser user)
        => _audit.Log(AuditEventBuilder.From(_caller.Current, AuditCategories.Auth, action,
            targetType: "user", targetId: user.Id, targetName: user.UserName));

    /// <summary>
    /// Groups the shared key in fours for readability. Deliberately NOT lower-cased: base32 (RFC 4648)
    /// is an upper-case alphabet, and authenticator apps reject or mis-decode a lower-case secret on
    /// manual entry. The scaffolded Identity template lower-cases here; that is a display bug.
    /// </summary>
    private static string FormatKey(string key)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < key.Length; i += 4)
            sb.Append(key.AsSpan(i, Math.Min(4, key.Length - i))).Append(' ');
        return sb.ToString().Trim().ToUpperInvariant();
    }
}
