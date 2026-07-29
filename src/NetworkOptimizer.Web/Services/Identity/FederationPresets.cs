using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Canned federation provider templates (design doc 03). A preset is just a pre-filled
/// <see cref="FederationProvider"/> - zero special-case code downstream. The admin supplies the
/// tenant-specific Authority/ClientId/secret. NOTE: live validation against a real UniFi Identity
/// tenant (claim names, logout behaviour) is a release gate before shipping this preset.
/// </summary>
public static class FederationPresets
{
    /// <summary>"Sign in with UniFi Identity" over a custom OIDC app in the UID admin portal.</summary>
    public static FederationProvider UniFiIdentity() => new()
    {
        Type = FederationProviderType.Oidc,
        Scheme = "unifi-identity",
        DisplayName = "UniFi Identity",
        ButtonLabel = "Sign in with UniFi Identity",
        Enabled = false,
        UsePkce = true,
        ResponseType = "code",
        Scopes = "openid profile email groups",
        GetClaimsFromUserInfo = true,
        SubjectClaim = "sub",
        UsernameClaim = "preferred_username",
        DisplayNameClaim = "name",
        EmailClaim = "email",
        GroupsClaim = "groups",
        TrustIdpMfa = true,
        JitProvisioning = JitProvisioningMode.CreateOnFirstLogin,
        RoleMappingMode = RoleMappingMode.Manual,
    };

    /// <summary>Generic OIDC starting point (openid profile email + groups).</summary>
    public static FederationProvider GenericOidc() => new()
    {
        Type = FederationProviderType.Oidc,
        Scheme = "oidc",
        DisplayName = "OIDC IdP",
        ButtonLabel = "Sign in with SSO",
        Enabled = false,
        UsePkce = true,
        ResponseType = "code",
        Scopes = "openid profile email",
        SubjectClaim = "sub",
        UsernameClaim = "preferred_username",
        DisplayNameClaim = "name",
        EmailClaim = "email",
        GroupsClaim = "groups",
        JitProvisioning = JitProvisioningMode.Off,
        RoleMappingMode = RoleMappingMode.Manual,
    };
}
