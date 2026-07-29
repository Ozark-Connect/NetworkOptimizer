using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// The key an external identity is stored and looked up under - ASP.NET Identity's
/// <c>LoginProvider</c>. It is the provider's scheme namespaced by protocol, because a scheme name is
/// only unique within its protocol and the two must never collide.
///
/// It lives here because it has to be the SAME string on both sides. Sign-in computed it privately
/// while the admin linking UI passed the bare scheme, so a hand-linked identity was written under
/// "auth0" and looked up under "oidc:auth0" - the link existed, was visible in the UI, and could never
/// match a login.
/// </summary>
public static class FederationSchemeKey
{
    /// <summary>The LoginProvider key for a provider.</summary>
    public static string For(FederationProvider provider) => For(provider.Type, provider.Scheme);

    /// <summary>The LoginProvider key for a protocol and scheme.</summary>
    public static string For(FederationProviderType type, string scheme)
        => (type == FederationProviderType.Saml ? "saml:" : "oidc:") + scheme;
}
