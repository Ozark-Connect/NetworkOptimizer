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

    /// <summary>
    /// Identifies the COOKIE a principal was built from - a fresh value every time one is issued, so
    /// two browsers signed in as the same account never share one, and every circuit of a single
    /// browser does.
    ///
    /// It exists so a self-service revocation can spare the session that asked for it. Changing your
    /// own password revokes every session for the account including your own, and the replacement
    /// cookie cannot reach the browser before the revocation reaches its circuits - so without a way
    /// to name the asking session, the browser that changed the password is signed out by its own
    /// action. Ordering alone does not fix it: a SignalR message beats an HTTP round trip every time.
    /// </summary>
    public const string SessionId = "netopt:sid";
}
