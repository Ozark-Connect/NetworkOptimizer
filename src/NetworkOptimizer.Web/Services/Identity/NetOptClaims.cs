namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Custom claim types added to the signed-in principal beyond Identity's defaults. Site memberships
/// are deliberately NOT claims (they change independently and can be many); only the membership
/// version stamp rides on the principal so caches and live circuits can detect drift (design doc 04).
/// </summary>
public static class NetOptClaims
{
    /// <summary>Snapshot of <see cref="Storage.Models.Identity.ApplicationUser.MembershipVersion"/> at sign-in.</summary>
    public const string MembershipVersion = "netopt:mv";

    /// <summary>Authentication method recorded for audit (password | totp | passkey | oidc:&lt;scheme&gt; | saml:&lt;scheme&gt; | recovery).</summary>
    public const string AuthMethod = "netopt:amr";

    /// <summary>
    /// Present when a role the user holds requires a second factor and they have enrolled none, so
    /// the session may reach the account security page to enrol and nothing else. Recomputed by
    /// <see cref="AppUserClaimsPrincipalFactory"/> every time a principal is built, never recorded at
    /// sign-in: a marker stamped once would be washed off by the next cookie refresh, which is a
    /// request any signed-in caller can make. Absent for everyone on an install where no role has
    /// Require MFA set.
    /// </summary>
    public const string MfaSetupPending = "netopt:mfa_setup";
}
