using Microsoft.AspNetCore.Antiforgery;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Identity;

namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// Interactive sign-in/out endpoints. These are HTTP (SSR) form posts / minimal endpoints, not Blazor
/// circuit handlers, because issuing or clearing the Identity cookie writes <c>Set-Cookie</c>, which a
/// live circuit cannot do (design docs 02, 06). All are anonymous by design and live under
/// <c>/api/auth/*</c>.
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        // Local username/password sign-in. Posted by the static-SSR Login page. On success the
        // application cookie is set and we redirect to the (validated, site-stamped) return URL; every
        // failure mode redirects back to /login with a single generic error (no username enumeration).
        app.MapPost("/api/auth/login", async (
            HttpContext context,
            IIdentitySignInService signInService,
            SiteSwitchService siteSwitch,
            IAntiforgery antiforgery) =>
        {
            var form = await context.Request.ReadFormAsync();
            var username = form["username"].ToString().Trim();
            var password = form["password"].ToString();
            var rememberMe = form["rememberMe"].ToString() is "true" or "on";
            var site = form[SiteContextService.SiteQueryParam].ToString();
            var returnUrl = SanitizeReturnUrl(form["returnUrl"].ToString());

            // This is a minimal-API endpoint (not a Razor component), so antiforgery is validated
            // explicitly here against the token the static-SSR login form emits (<AntiforgeryToken/>).
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return LoginRedirect("invalid", site);
            }

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                return LoginRedirect("missing", site);

            var outcome = await signInService.PasswordSignInAsync(username, password, rememberMe);
            return outcome switch
            {
                // Carry the tab's ?site= pin from the form field (the POST has no ?site= query of its
                // own); fall back to the request's resolved site when the field is absent.
                SignInOutcome.Success => Results.Redirect(string.IsNullOrEmpty(site)
                    ? await siteSwitch.StampSiteAsync(returnUrl)
                    : SiteContextService.WithSiteParam(returnUrl, site)),
                SignInOutcome.RequiresTwoFactor => Results.Redirect(TwoFactorRedirect(returnUrl, site)),
                SignInOutcome.RequiresMfaEnrollment => Results.Redirect("/account/security?setup=required"),
                SignInOutcome.LockedOut => LoginRedirect("lockout", site),
                SignInOutcome.LocalLoginDisabled => LoginRedirect("sso_only", site),
                _ => LoginRedirect("invalid", site),
            };
        });

        // Second-factor step (TOTP or recovery code), posted by the /login/2fa static-SSR page.
        app.MapPost("/api/auth/2fa", async (
            HttpContext context, IIdentitySignInService signInService, SiteSwitchService siteSwitch, IAntiforgery antiforgery) =>
        {
            var form = await context.Request.ReadFormAsync();
            var site = form[SiteContextService.SiteQueryParam].ToString();
            var returnUrl = SanitizeReturnUrl(form["returnUrl"].ToString());
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return LoginRedirect("invalid", site); }

            var rememberMachine = form["rememberMachine"].ToString() is "true" or "on";
            var recoveryCode = form["recoveryCode"].ToString();
            var outcome = string.IsNullOrEmpty(recoveryCode)
                ? await signInService.TwoFactorSignInAsync(form["code"].ToString(), rememberMe: false, rememberMachine)
                : await signInService.RecoveryCodeSignInAsync(recoveryCode);

            return outcome switch
            {
                SignInOutcome.Success => Results.Redirect(string.IsNullOrEmpty(site)
                    ? await siteSwitch.StampSiteAsync(returnUrl)
                    : SiteContextService.WithSiteParam(returnUrl, site)),
                SignInOutcome.LockedOut => LoginRedirect("lockout", site),
                _ => Results.Redirect(TwoFactorRedirect(returnUrl, site) + "&error=invalid"),
            };
        });

        // Sign out of the application cookie. GET is retained so existing nav logout links keep working;
        // it also clears any residual legacy auth_token cookie during the bridge window.
        app.MapGet("/api/auth/logout", async (HttpContext context, IIdentitySignInService signInService) =>
        {
            await signInService.SignOutAsync();
            context.Response.Cookies.Delete("auth_token", new CookieOptions { Path = "/" });
            var site = context.Request.Query[SiteContextService.SiteQueryParam].ToString();
            return LoginRedirect(null, site);
        });
    }

    /// <summary>Builds a redirect to /login, carrying the tab's ?site= pin and an optional error code.</summary>
    private static IResult LoginRedirect(string? error, string site)
    {
        var query = new List<string>();
        if (!string.IsNullOrEmpty(error))
            query.Add($"error={error}");
        if (!string.IsNullOrEmpty(site))
            query.Add($"{SiteContextService.SiteQueryParam}={Uri.EscapeDataString(site)}");
        var suffix = query.Count > 0 ? "?" + string.Join("&", query) : "";
        return Results.Redirect($"/login{suffix}");
    }

    private static string TwoFactorRedirect(string returnUrl, string site)
    {
        var query = new List<string> { $"returnUrl={Uri.EscapeDataString(returnUrl)}" };
        if (!string.IsNullOrEmpty(site))
            query.Add($"{SiteContextService.SiteQueryParam}={Uri.EscapeDataString(site)}");
        return $"/login/2fa?{string.Join("&", query)}";
    }

    /// <summary>Rejects open-redirect targets: only same-site relative paths are allowed.</summary>
    private static string SanitizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl) ||
            !returnUrl.StartsWith('/') ||
            returnUrl.StartsWith("//") ||
            returnUrl.Contains(':'))
        {
            return "/";
        }
        return returnUrl;
    }
}
