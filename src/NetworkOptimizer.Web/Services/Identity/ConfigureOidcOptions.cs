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
    private readonly ILogger<ConfigureOidcOptions> _logger;

    public ConfigureOidcOptions(IServiceScopeFactory scopeFactory, ILogger<ConfigureOidcOptions> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Why this provider's row cannot drive the OIDC handler, or null when it can. The handler validates
    /// its options on every request (see <see cref="Configure(string?, OpenIdConnectOptions)"/>), so a row
    /// that fails here must never reach it: a plain-http or malformed Authority would otherwise throw on
    /// every request to every route, sign-in page included, and keep doing so across restarts.
    /// </summary>
    public static string? ConfigurationProblem(FederationProvider provider)
    {
        if (string.IsNullOrWhiteSpace(provider.Authority))
            return "An OIDC provider needs an Authority (the issuer URL).";
        // Both checks: the handler tests the literal "https://" prefix of the metadata address it
        // derives from the Authority, while Uri also accepts "https:\\host" and reports https for it.
        var trimmed = provider.Authority.Trim();
        if (!trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var authority)
            || authority.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(authority.Host))
            return "The Authority must be an https:// address.";
        if (string.IsNullOrWhiteSpace(provider.ClientId))
            return "An OIDC provider needs a Client ID.";
        return null;
    }

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
            // whose provider was removed).
            ApplyInert(options, schemeKey);
            return;
        }

        // A row that predates save-time validation, or arrived some other way (a restored database),
        // gets the same inert options rather than taking the whole app down. The sign-in endpoint
        // refuses to challenge it, so the only effect is that this one provider does not work.
        if (ConfigurationProblem(provider) is { } problem)
        {
            _logger.LogWarning(
                "OIDC provider {Provider} cannot be used and is held inert: {Problem}",
                provider.DisplayName, problem);
            ApplyInert(options, schemeKey);
            return;
        }

        options.Authority = provider.Authority!.Trim();
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
        // Null-or-empty: these are optional fields, and clearing one in the UI stores "" rather than
        // null. A plain ?? left the scopes blank, which asks the provider for nothing at all.
        var scopes = string.IsNullOrWhiteSpace(provider.Scopes) ? "openid profile email" : provider.Scopes;
        foreach (var s in scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            options.Scope.Add(s);

        options.TokenValidationParameters.NameClaimType = string.IsNullOrWhiteSpace(provider.UsernameClaim)
            ? "preferred_username"
            : provider.UsernameClaim.Trim();
        options.MapInboundClaims = false; // keep raw claim types for our claim mapping

        options.Events ??= new OpenIdConnectEvents();

        // Without this a user who cancels at the IdP, or comes back after the correlation cookie
        // expired, gets the unhandled-exception page. Send them back to sign-in with the message the
        // callback already uses for an incomplete external sign-in.
        var logger = _logger;
        options.Events.OnRemoteFailure = context =>
        {
            logger.LogWarning("OIDC sign-in via {Scheme} did not complete: {Reason}",
                context.Scheme.Name, context.Failure?.Message ?? "(no reason given)");
            context.Response.Redirect("/login?error=external_failed");
            context.HandleResponse();
            return Task.CompletedTask;
        };

        // The handler builds redirect_uri from the incoming request, which behind a reverse proxy is
        // plain HTTP on 8042 - so it sends http://... and the provider rejects it for not matching the
        // registered callback. Use the address the operator declared this install is reached at.
        var canonical = scope.ServiceProvider.GetRequiredService<ICanonicalOrigin>();
        if (canonical.Configured is not null)
        {
            var signIn = canonical.ConfiguredUriFor(options.CallbackPath);
            var signOut = canonical.ConfiguredUriFor(options.SignedOutCallbackPath);

            options.Events.OnRedirectToIdentityProvider = context =>
            {
                context.ProtocolMessage.RedirectUri = signIn;
                return Task.CompletedTask;
            };

            options.Events.OnRedirectToIdentityProviderForSignOut = context =>
            {
                context.ProtocolMessage.PostLogoutRedirectUri = signOut;
                return Task.CompletedTask;
            };
        }
    }

    /// <summary>Whether a provider can sign anyone in: any SAML provider, or an OIDC one the handler can run.</summary>
    public static bool IsUsable(FederationProvider provider)
        => provider.Type != FederationProviderType.Oidc || ConfigurationProblem(provider) is null;

    /// <summary>
    /// Valid-but-inert options. The OIDC handler is an IAuthenticationRequestHandler, so
    /// UseAuthentication initializes it on EVERY request to test for its callback path - which validates
    /// the options. These pass validation; the scheme is never challenged, so it never touches the
    /// network.
    /// </summary>
    private static void ApplyInert(OpenIdConnectOptions options, string schemeKey)
    {
        options.ClientId = "unconfigured";
        options.Authority = "https://localhost/";
        options.RequireHttpsMetadata = false;
        options.CallbackPath = $"/signin-oidc-unconfigured/{schemeKey}";
        options.SignInScheme = IdentityConstants.ExternalScheme;
    }
}
