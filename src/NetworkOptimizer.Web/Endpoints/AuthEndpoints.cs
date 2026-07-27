using Microsoft.AspNetCore.Antiforgery;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Identity;

using NetworkOptimizer.Web.Services.Authorization;

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
            IMfaService mfa,
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
                // Resolved from the submitted username: the two-factor cookie was written into this
                // response, so it cannot be read back out of this request.
                SignInOutcome.RequiresTwoFactor => Results.Redirect(
                    TwoFactorRedirect(returnUrl, site, await mfa.HasRecoveryCodesAsync(username), await mfa.HasPasskeysAsync(username), rememberMe)),
                SignInOutcome.RequiresMfaEnrollment => Results.Redirect("/account/security?setup=required"),
                SignInOutcome.RequiresPasskeySignIn => LoginRedirect("use_passkey", site),
                SignInOutcome.LockedOut => LoginRedirect("lockout", site),
                SignInOutcome.LocalLoginDisabled => LoginRedirect("sso_only", site),
                _ => LoginRedirect("invalid", site),
            };
        })
            .AllowAnonymous().RequireRateLimiting("Authentication");

        // Second-factor step (TOTP or recovery code), posted by the /login/2fa static-SSR page.
        app.MapPost("/api/auth/2fa", async (
            HttpContext context, IIdentitySignInService signInService, SiteSwitchService siteSwitch,
            IMfaService mfa, IAntiforgery antiforgery) =>
        {
            var form = await context.Request.ReadFormAsync();
            var site = form[SiteContextService.SiteQueryParam].ToString();
            var returnUrl = SanitizeReturnUrl(form["returnUrl"].ToString());
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return LoginRedirect("invalid", site); }

            var rememberMachine = form["rememberMachine"].ToString() is "true" or "on";
            // Carried from the first page: the final sign-in happens here, so without it an
            // account with a second factor would always get a session-scoped cookie.
            var stayedSignedIn = form["rememberMe"].ToString() is "true" or "on";
            var recoveryCode = form["recoveryCode"].ToString();
            var outcome = string.IsNullOrEmpty(recoveryCode)
                ? await signInService.TwoFactorSignInAsync(form["code"].ToString(), stayedSignedIn, rememberMachine)
                : await signInService.RecoveryCodeSignInAsync(recoveryCode);

            return outcome switch
            {
                SignInOutcome.Success => Results.Redirect(string.IsNullOrEmpty(site)
                    ? await siteSwitch.StampSiteAsync(returnUrl)
                    : SiteContextService.WithSiteParam(returnUrl, site)),
                SignInOutcome.LockedOut => LoginRedirect("lockout", site),
                _ => Results.Redirect(
                    TwoFactorRedirect(returnUrl, site, await PendingUserHasRecoveryCodesAsync(mfa), await PendingUserHasPasskeysAsync(mfa), stayedSignedIn) + "&error=invalid"),
            };
        })
            .AllowAnonymous().RequireRateLimiting("Authentication");

        // Completing TOTP enrollment flips two-factor on, which rotates the security stamp and leaves
        // the caller's cookie stale - the live circuit revalidates as signed out and the app starts
        // erroring until they sign in again. A circuit cannot write Set-Cookie, so enrollment finishes
        // here, over HTTP, and the cookie is refreshed in the same request (design doc 06: sign-in
        // flows are HTTP, not circuit). The recovery codes are handed to the page through a one-time
        // store rather than the URL, so they never land in history or a proxy log.
        app.MapPost("/api/auth/mfa/enable", async (
            HttpContext context,
            IMfaService mfa,
            ICurrentUserAccessor currentUser,
            IIdentitySignInService signInService,
            MfaEnrollmentCodes enrollmentCodes,
            IAntiforgery antiforgery) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.Redirect("/account/security?enroll=failed");
            }

            var user = await currentUser.GetAsync(context.User);
            if (user is null)
                return Results.Redirect("/login");

            var form = await context.Request.ReadFormAsync();
            var code = form["code"].ToString().Trim();
            if (!await mfa.CompleteEnrollmentAsync(user, code))
                return Results.Redirect("/account/security?enroll=failed");

            var codes = await mfa.GenerateRecoveryCodesAsync(user);
            enrollmentCodes.Stash(user.Id, codes);

            // The stamp moved when two-factor was enabled; re-issue the cookie against the new one.
            await signInService.RefreshSignInAsync(user);
            return Results.Redirect("/account/security?enroll=done");
        })
            // Enrolling your own second factor: signed in is the only requirement. A user in the
            // must-enroll state already holds a cookie, so this stays reachable for them.
            .RequireAuthorization(Policies.RequireViewer);

        // Self-service password change. Posted rather than handled in the circuit for the same reason
        // as enrolment: changing a password rotates the security stamp, and only an HTTP response can
        // re-issue the cookie carrying it - without that the user is signed out within the
        // revalidation interval for succeeding.
        app.MapPost("/api/account/password", async (
            HttpContext context,
            ICurrentUserAccessor currentUser,
            IIdentityAdminService identityAdmin,
            IIdentitySignInService signInService,
            IAntiforgery antiforgery) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.Redirect("/account/security?pw=failed");
            }

            var user = await currentUser.GetAsync(context.User);
            if (user is null)
                return Results.Redirect("/login");

            var form = await context.Request.ReadFormAsync();
            var current = form["currentPassword"].ToString();
            var next = form["newPassword"].ToString();

            // Told apart from a plain failure so the page can say why rather than sending someone to
            // re-check a current password that was right.
            if (string.Equals(current, next, StringComparison.Ordinal))
                return Results.Redirect("/account/security?pw=same");

            var result = await identityAdmin.ChangeOwnPasswordAsync(user.Id, current, next);

            if (!result.Succeeded)
                return Results.Redirect("/account/security?pw=failed");

            await signInService.RefreshSignInAsync(user);
            return Results.Redirect("/account/security?pw=done");
        })
            .RequireAuthorization(Policies.RequireViewer);

        // Sign out of the application cookie; also clears any residual legacy auth_token cookie
        // during the bridge window. POST, not GET: a GET logout can be fired by any page embedding
        // <img src=".../api/auth/logout">, and by link prefetchers. Posting from a form also keeps
        // the Blazor router out of it - it used to try to route this endpoint as a page and throw.
        app.MapPost("/api/auth/logout", async (
            HttpContext context,
            IIdentitySignInService signInService,
            IAntiforgery antiforgery) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.Redirect("/login");
            }

            await signInService.SignOutAsync();
            context.Response.Cookies.Delete("auth_token", new CookieOptions { Path = "/" });
            var site = context.Request.Form[SiteContextService.SiteQueryParam].ToString();
            return LoginRedirect(null, site);
        })
            .AllowAnonymous();
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

    /// <summary>
    /// Builds the second-factor page URL. <paramref name="hasRecoveryCodes"/> travels in the query
    /// because the page renders twice - prerendered, then interactive - and only the prerender pass
    /// has an HttpContext to read the pending two-factor cookie from. Resolving it here, once, keeps
    /// both passes agreeing.
    /// </summary>
    private static string TwoFactorRedirect(string returnUrl, string site, bool hasRecoveryCodes, bool hasPasskey, bool rememberMe)
    {
        var query = new List<string> { $"returnUrl={Uri.EscapeDataString(returnUrl)}" };
        if (!string.IsNullOrEmpty(site))
            query.Add($"{SiteContextService.SiteQueryParam}={Uri.EscapeDataString(site)}");
        if (hasRecoveryCodes)
            query.Add("rc=true");
        if (hasPasskey)
            query.Add("pk=true");
        if (rememberMe)
            query.Add("rm=true");
        return $"/login/2fa?{string.Join("&", query)}";
    }

    /// <summary>Whether the account waiting on the second factor holds any recovery codes.</summary>
    private static async Task<bool> PendingUserHasRecoveryCodesAsync(IMfaService mfa)
    {
        var pending = await mfa.GetPendingTwoFactorUserAsync();
        return pending is not null && await mfa.CountRecoveryCodesAsync(pending) > 0;
    }

    private static async Task<bool> PendingUserHasPasskeysAsync(IMfaService mfa)
    {
        var pending = await mfa.GetPendingTwoFactorUserAsync();
        return pending is not null && await mfa.HasPasskeysAsync(pending.UserName ?? "");
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
