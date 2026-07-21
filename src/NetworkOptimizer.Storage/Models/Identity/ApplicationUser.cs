using Microsoft.AspNetCore.Identity;

namespace NetworkOptimizer.Storage.Models.Identity;

/// <summary>
/// Application user record. Slim extension of ASP.NET Core Identity's <see cref="IdentityUser"/>
/// (string GUID key). Email is optional (self-hosted boxes often have no SMTP), usernames are the
/// primary identifier, and <see cref="IdentityUser.PasswordHash"/> is null for federated-only users
/// (JIT-provisioned). See research design docs 02 (identity/authn) and 04 (RBAC).
/// </summary>
public class ApplicationUser : IdentityUser
{
    /// <summary>Optional friendly name shown in the UI; falls back to <see cref="IdentityUser.UserName"/>.</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Local enablement gate. A disabled user cannot sign in even with a valid federated IdP
    /// session (deprovisioning v1). Distinct from Identity lockout.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>When the account was created (UTC).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Last successful sign-in (UTC), or null if never signed in.</summary>
    public DateTime? LastLoginAt { get; set; }

    /// <summary>
    /// Method of the last successful sign-in for display/audit context
    /// (password | totp | passkey | oidc:&lt;scheme&gt; | saml:&lt;scheme&gt; | recovery).
    /// </summary>
    public string? LastLoginMethod { get; set; }

    /// <summary>
    /// True when the account's password is the auto-generated first-run temporary password carried
    /// over at migration (the "lazy homelabber" who never set their own). Drives the un-dismissable
    /// "set a real password" nag; cleared the first time the user sets their own password. Preserves
    /// the pre-Identity auto-generated-password UX (design doc 02 migration).
    /// </summary>
    public bool PasswordIsTemporary { get; set; }

    /// <summary>
    /// Membership version stamp. Bumped whenever this user's site memberships, group
    /// assignments, or the groups they derive access from change, so cached authorized-slug
    /// sets and live Blazor circuits invalidate. Distinct from Identity's <see cref="IdentityUser.SecurityStamp"/>,
    /// which covers credential/global-role/2FA revocation; this covers per-site membership drift.
    /// </summary>
    public int MembershipVersion { get; set; } = 1;
}
