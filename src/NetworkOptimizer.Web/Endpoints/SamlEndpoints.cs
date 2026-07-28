using Microsoft.AspNetCore.Identity;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Identity;

namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// SAML 2.0 service-provider endpoints (design doc 03): auto-generated SP metadata, the SP-initiated
/// AuthnRequest, and the ACS that validates the IdP response and hands the asserted principal to
/// <see cref="IExternalLoginService"/>. IdP-initiated SSO is off unless the provider opts in. All
/// anonymous by design (this is a login path); signed assertions are required by the SP config.
/// </summary>
public static class SamlEndpoints
{
    public static void MapSamlEndpoints(this WebApplication app)
    {
        // SP metadata for the IdP admin to consume.
        app.MapGet("/saml/{scheme}/metadata", async (
            string scheme, HttpContext ctx, IFederationProviderService providers, ISamlServiceProvider saml) =>
        {
            var provider = await SamlProviderAsync(providers, scheme);
            if (provider is null) return Results.NotFound();
            return Results.Content(await saml.BuildMetadataAsync(provider, ctx), "application/samlmetadata+xml");
        })
            .AllowAnonymous();

        // SP-initiated login.
        app.MapGet("/login/saml/{scheme}", async (
            string scheme, HttpContext ctx, IFederationProviderService providers, ISamlServiceProvider saml) =>
        {
            var provider = await SamlProviderAsync(providers, scheme);
            if (provider is null || !provider.Enabled) return Results.Redirect("/login?error=provider_disabled");
            return await saml.ChallengeAsync(provider, ctx, returnUrl: "/");
        })
            .AllowAnonymous();

        // Assertion Consumer Service: validate the IdP response and resolve to a local user.
        app.MapPost("/saml/{scheme}/acs", async (
            string scheme, HttpContext ctx,
            IFederationProviderService providers, ISamlServiceProvider saml, IExternalLoginService externalLogin) =>
        {
            var provider = await SamlProviderAsync(providers, scheme);
            if (provider is null || !provider.Enabled) return Results.Redirect("/login?error=provider_disabled");

            // IdP-initiated responses are refused unless explicitly opted in (unsolicited-response risk).
            // SP-initiated flows carry RelayState; its absence signals an unsolicited (IdP-initiated) POST.
            if (!provider.AllowIdpInitiated && string.IsNullOrEmpty(ctx.Request.Form["RelayState"]))
                return Results.Redirect("/login?error=saml_unsolicited");

            var principal = await saml.HandleAssertionAsync(provider, ctx);
            if (principal is null) return Results.Redirect("/login?error=saml_invalid");

            var outcome = await externalLogin.ProcessAsync(provider, principal);
            return outcome switch
            {
                ExternalLoginOutcome.SignedIn => Results.Redirect("/"),
                ExternalLoginOutcome.Disabled => Results.Redirect("/login?error=account_disabled"),
                _ => Results.Redirect("/login?error=no_account"),
            };
        })
            .AllowAnonymous();
    }

    private static async Task<FederationProvider?> SamlProviderAsync(IFederationProviderService providers, string scheme)
    {
        var provider = await providers.GetBySchemeAsync(scheme);
        return provider?.Type == FederationProviderType.Saml ? provider : null;
    }
}
