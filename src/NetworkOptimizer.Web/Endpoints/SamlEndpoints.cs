using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
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
            return await saml.ChallengeAsync(
                provider, ctx, returnUrl: "/", rememberMe: FederationEndpoints.RememberMeRequested(ctx));
        })
            .AllowAnonymous();

        // Assertion Consumer Service: validate the IdP response and resolve to a local user.
        app.MapPost("/saml/{scheme}/acs", async (
            string scheme, HttpContext ctx,
            IFederationProviderService providers, ISamlServiceProvider saml, IExternalLoginService externalLogin) =>
        {
            var provider = await SamlProviderAsync(providers, scheme);
            if (provider is null || !provider.Enabled) return Results.Redirect("/login?error=provider_disabled");

            // AllowIdpInitiated is enforced inside HandleAssertionAsync, against this server's own
            // record of the AuthnRequests it issued. It used to be decided here from the presence of
            // RelayState - but that is an attacker-supplied form field, so posting RelayState=x made
            // any unsolicited response look solicited and turned the setting off entirely. Nothing the
            // POST carries can be trusted for this, and none of it is signature-checked until Unbind.
            var relayState = ctx.Request.Form["RelayState"].ToString();

            var principal = await saml.HandleAssertionAsync(provider, ctx);
            if (principal is null) return Results.Redirect("/login?error=saml_invalid");

            var rememberMe = RememberMeFromRelayState(relayState);
            var outcome = await externalLogin.ProcessAsync(provider, principal, rememberMe);
            return outcome switch
            {
                ExternalLoginOutcome.SignedIn => Results.Redirect("/"),
                ExternalLoginOutcome.Disabled => Results.Redirect("/login?error=account_disabled"),
                // Same destinations local sign-in uses for the same policy, so the requirement reads
                // identically however the user arrived.
                ExternalLoginOutcome.RequiresMfaEnrollment => Results.Redirect("/account/security?setup=required"),
                ExternalLoginOutcome.RequiresPasskeySignIn => Results.Redirect("/login?error=use_passkey"),
                // The pending two-factor state is set, so the existing code entry page completes it.
                ExternalLoginOutcome.RequiresTwoFactor
                    => Results.Redirect(FederationEndpoints.TwoFactorPath(rememberMe)),
                ExternalLoginOutcome.RequiresLocalMfa => Results.Redirect("/login?error=mfa_local_required"),
                _ => Results.Redirect("/login?error=no_account"),
            };
        })
            .AllowAnonymous();
    }

    /// <summary>
    /// Recovers the Keep me signed in choice from RelayState, where the SP-initiated challenge put it.
    /// The IdP echoes RelayState back verbatim, so this is influenceable by whoever controls the
    /// response - it decides only how long a cookie that sign-in has already earned lasts, never
    /// whether one is issued.
    /// </summary>
    private static bool RememberMeFromRelayState(string relayState)
        => !string.IsNullOrEmpty(relayState)
            && QueryHelpers.ParseNullableQuery(relayState)?.TryGetValue("rm", out var rm) == true
            && rm.ToString() == "true";

    private static async Task<FederationProvider?> SamlProviderAsync(IFederationProviderService providers, string scheme)
    {
        var provider = await providers.GetBySchemeAsync(scheme);
        return provider?.Type == FederationProviderType.Saml ? provider : null;
    }
}
