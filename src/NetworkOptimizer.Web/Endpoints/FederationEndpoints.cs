using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using NetworkOptimizer.Web.Services.Identity;

namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// OIDC relying-party challenge/callback endpoints (design doc 03). The challenge redirects to the
/// IdP via the dynamically-registered <c>oidc:&lt;scheme&gt;</c> handler; the callback reads the external
/// principal from the Identity external cookie and hands it to <see cref="IExternalLoginService"/> for
/// linking/JIT-provisioning. Anonymous by design (this IS the login path).
/// </summary>
public static class FederationEndpoints
{
    public static void MapFederationEndpoints(this WebApplication app)
    {
        // Start an external login: challenge the provider's OIDC scheme.
        app.MapGet("/login/external/{scheme}", (string scheme) =>
        {
            var props = new AuthenticationProperties { RedirectUri = "/login/external-callback" };
            return Results.Challenge(props, new[] { ConfigureOidcOptions.Prefix + scheme });
        })
            .AllowAnonymous();

        // IdP returned and the OIDC handler wrote the external cookie; resolve to a local user.
        app.MapGet("/login/external-callback", async (
            HttpContext context, IFederationProviderService providers, IExternalLoginService externalLogin) =>
        {
            var result = await context.AuthenticateAsync(IdentityConstants.ExternalScheme);
            if (!result.Succeeded || result.Principal is null)
                return Results.Redirect("/login?error=external_failed");

            // The scheme that produced this ticket is recorded in the properties.
            var schemeName = result.Properties?.Items[".AuthScheme"];
            if (string.IsNullOrEmpty(schemeName) || !schemeName.StartsWith(ConfigureOidcOptions.Prefix, StringComparison.Ordinal))
                return Results.Redirect("/login?error=external_failed");

            var providerScheme = schemeName[ConfigureOidcOptions.Prefix.Length..];
            var provider = await providers.GetBySchemeAsync(providerScheme);
            if (provider is null || !provider.Enabled)
                return Results.Redirect("/login?error=provider_disabled");

            var outcome = await externalLogin.ProcessAsync(provider, result.Principal);

            // Clear the transient external cookie now that we've consumed it.
            await context.SignOutAsync(IdentityConstants.ExternalScheme);

            return outcome switch
            {
                ExternalLoginOutcome.SignedIn => Results.Redirect("/"),
                ExternalLoginOutcome.Disabled => Results.Redirect("/login?error=account_disabled"),
                // Same destinations local sign-in uses for the same policy, so the requirement reads
                // identically however the user arrived.
                ExternalLoginOutcome.RequiresMfaEnrollment => Results.Redirect("/account/security?setup=required"),
                ExternalLoginOutcome.RequiresPasskeySignIn => Results.Redirect("/login?error=use_passkey"),
                // The pending two-factor state is set, so the existing code entry page completes it.
                ExternalLoginOutcome.RequiresTwoFactor => Results.Redirect("/login/2fa?returnUrl=%2F"),
                ExternalLoginOutcome.RequiresLocalMfa => Results.Redirect("/login?error=mfa_local_required"),
                _ => Results.Redirect("/login?error=no_account"),
            };
        })
            .AllowAnonymous();
    }
}
