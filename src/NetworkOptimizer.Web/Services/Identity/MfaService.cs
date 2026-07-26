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

    /// <summary>Disables MFA and clears the authenticator key.</summary>
    Task DisableAsync(ApplicationUser user);
}

/// <inheritdoc />
public sealed class MfaService : IMfaService
{
    private const string Issuer = "Network Optimizer";
    private const int RecoveryCodeCount = 10;

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly IAuditLogger _audit;
    private readonly ICallerContext _caller;

    public MfaService(
        UserManager<ApplicationUser> userManager,
        RoleManager<ApplicationRole> roleManager,
        IAuditLogger audit,
        ICallerContext caller)
    {
        _userManager = userManager;
        _roleManager = roleManager;
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

        await _userManager.SetTwoFactorEnabledAsync(user, true);
        Emit(AuditActions.MfaEnrolled, user);
        return true;
    }

    public async Task<IReadOnlyList<string>> GenerateRecoveryCodesAsync(ApplicationUser user)
    {
        var codes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount);
        Emit(AuditActions.RecoveryCodesRegenerated, user);
        return codes?.ToList() ?? new List<string>();
    }

    public async Task DisableAsync(ApplicationUser user)
    {
        await _userManager.SetTwoFactorEnabledAsync(user, false);
        await _userManager.ResetAuthenticatorKeyAsync(user);
        Emit(AuditActions.MfaRemoved, user);
    }

    private void Emit(string action, ApplicationUser user)
        => _audit.Log(AuditEventBuilder.From(_caller.Current, AuditCategories.Auth, action,
            targetType: "user", targetId: user.Id, targetName: user.UserName));

    private static string FormatKey(string key)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < key.Length; i += 4)
            sb.Append(key.AsSpan(i, Math.Min(4, key.Length - i))).Append(' ');
        return sb.ToString().Trim().ToLowerInvariant();
    }
}
