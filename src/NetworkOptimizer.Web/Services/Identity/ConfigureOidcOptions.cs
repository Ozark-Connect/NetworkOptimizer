using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Configures the OpenID Connect options for a dynamically-registered provider scheme
/// (<c>oidc:&lt;scheme&gt;</c>) by loading its <see cref="FederationProvider"/> from the DB (design doc
/// 03). The client secret is decrypted here and never logged. The external principal lands in the
/// Identity external cookie; the federation callback endpoint then links/JIT-provisions it.
/// </summary>
public sealed class ConfigureOidcOptions : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public const string Prefix = "oidc:";

    private readonly IServiceScopeFactory _scopeFactory;

    public ConfigureOidcOptions(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public void Configure(OpenIdConnectOptions options) { }

    public void Configure(string? name, OpenIdConnectOptions options)
    {
        if (name is null || !name.StartsWith(Prefix, StringComparison.Ordinal))
            return;

        var schemeKey = name[Prefix.Length..];
        using var scope = _scopeFactory.CreateScope();
        var providers = scope.ServiceProvider.GetRequiredService<IFederationProviderService>();
        var provider = providers.GetBySchemeAsync(schemeKey).GetAwaiter().GetResult();
        if (provider is null || provider.Type != FederationProviderType.Oidc)
        {
            // Unconfigured/placeholder scheme (e.g. the handler-registration "__template__", or a scheme
            // whose provider was removed). The OIDC handler is an IAuthenticationRequestHandler, so
            // UseAuthentication initializes it on EVERY request to test for its callback path - which
            // validates the options. Give it valid-but-inert values so validation passes; it is never
            // challenged, and a static empty Configuration means it never touches the network.
            options.ClientId = "unconfigured";
            options.Authority = "https://localhost/";
            options.RequireHttpsMetadata = false;
            options.CallbackPath = $"/signin-oidc-unconfigured/{schemeKey}";
            options.SignInScheme = IdentityConstants.ExternalScheme;
            return;
        }

        options.Authority = provider.Authority;
        options.ClientId = provider.ClientId;
        options.ClientSecret = providers.UnprotectClientSecret(provider);
        options.ResponseType = "code";
        options.UsePkce = provider.UsePkce;
        options.SaveTokens = true;
        options.GetClaimsFromUserInfoEndpoint = provider.GetClaimsFromUserInfo;
        options.CallbackPath = $"/signin-oidc/{schemeKey}";
        options.SignInScheme = IdentityConstants.ExternalScheme;
        options.SignedOutCallbackPath = $"/signout-callback-oidc/{schemeKey}";
        options.RemoteSignOutPath = $"/signout-oidc/{schemeKey}";

        options.Scope.Clear();
        foreach (var s in (provider.Scopes ?? "openid profile email").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            options.Scope.Add(s);

        options.TokenValidationParameters.NameClaimType = provider.UsernameClaim ?? "preferred_username";
        options.MapInboundClaims = false; // keep raw claim types for our claim mapping
    }
}
