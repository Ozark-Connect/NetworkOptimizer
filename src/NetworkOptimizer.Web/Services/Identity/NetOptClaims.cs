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
}
