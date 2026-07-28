using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Authorization;
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
            // must-enroll state holds a cookie every other gate refuses, so this has to be the one
            // policy that admits it - otherwise the requirement could never be satisfied.
            .RequireAuthorization(Policies.AccountSelfService);

        // Self-service password change. Posted rather than handled in the circuit for the same reason
        // as enrolment: changing a password rotates the security stamp, and only an HTTP response can
        // re-issue the cookie carrying it - without that the user is signed out within the
        // revalidation interval for succeeding.
        app.MapPost("/api/account/password", async (
            HttpContext context,
            ICurrentUserAccessor currentUser,
            IIdentityAdminService identityAdmin,
            IIdentitySignInService signInService,
            UserSessionRevocationNotifier revocations,
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

            // Changing the password rotates the security stamp, so every other session is already
            // invalid - but the stamp is only re-checked on an interval, so without this they keep
            // working for up to five minutes after the credential they hold was replaced.
            //
            // Announced once THIS response is on the wire, not before it. The watcher sends every
            // circuit for the account to revalidate, and the calling browser's own circuit is one of
            // them - announcing inline sends it there still holding the cookie this request is in the
            // middle of replacing, so the browser that changed its password is the one that gets
            // signed out.
            RevokeAfterResponse(context, revocations, user.Id);
            return Results.Redirect("/account/security?pw=done");
        })
            .RequireAuthorization(Policies.AccountSelfService);

        // Ends every other session for the signed-in user. Posted from a form for the same reason the
        // password change is: rotating the stamp invalidates this browser's cookie too, and only an
        // HTTP response can hand back a fresh one.
        app.MapPost("/api/account/sessions/revocation", async (
            HttpContext context,
            ICurrentUserAccessor currentUser,
            IIdentityAdminService identityAdmin,
            IIdentitySignInService signInService,
            UserSessionRevocationNotifier revocations,
            IAntiforgery antiforgery) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.Redirect("/account/security?sessions=failed");
            }

            var user = await currentUser.GetAsync(context.User);
            if (user is null)
                return Results.Redirect("/login");

            var result = await identityAdmin.SignOutEverywhereAsync(user.Id);
            if (!result.Succeeded)
                return Results.Redirect("/account/security?sessions=failed");

            await signInService.RefreshSignInAsync(user);

            // Announced only once this response is on the wire, and deliberately not inside the
            // service: every other circuit for this account is about to be sent to revalidate, and the
            // browser that asked has to be holding its re-issued cookie before that happens or it
            // races its own other tabs - and itself - out.
            RevokeAfterResponse(context, revocations, user.Id);
            return Results.Redirect("/account/security?sessions=done");
        })
            .RequireAuthorization(Policies.AccountSelfService);

        // Settles whether this cookie is still good, right now. The cookie's security stamp is only
        // re-checked on an interval (5 min), so rotating the stamp does NOT make the cookie stop
        // working at that instant - a reload inside the window is waved straight through, which is
        // why disabling an account and signing out everywhere both looked like they did nothing. A
        // revoked circuit is sent here instead of merely reloading, and this asks the question the
        // interval was going to get round to asking.
        app.MapGet("/api/account/revalidate", async (
            HttpContext context,
            ICurrentUserAccessor currentUser,
            UserManager<ApplicationUser> userManager,
            IIdentitySignInService signInService,
            IOptions<IdentityOptions> identityOptions) =>
        {
            var user = await currentUser.GetAsync(context.User);

            var stillValid = user is not null && user.IsEnabled;
            if (stillValid && userManager.SupportsUserSecurityStamp)
            {
                var onCookie = context.User.FindFirstValue(
                    identityOptions.Value.ClaimsIdentity.SecurityStampClaimType);
                stillValid = onCookie == await userManager.GetSecurityStampAsync(user!);
            }

            if (!stillValid)
            {
                await signInService.SignOutAsync();
                return Results.Redirect("/login?error=session_ended");
            }

            return Results.LocalRedirect(SanitizeReturnUrl(context.Request.Query["returnUrl"].ToString()));
        })
            .RequireAuthorization(Policies.AccountSelfService);

        // Re-issues the caller's own cookie from the current store: new roles, new membership version.
        // A circuit lands here when it notices its permissions have moved on, and goes straight back
        // to the page it was on. GET because it is the caller renewing their own session and nothing
        // else - it grants nothing that signing out and in again would not.
        app.MapGet("/api/account/session", async (
            HttpContext context,
            ICurrentUserAccessor currentUser,
            UserManager<ApplicationUser> userManager,
            IIdentitySignInService signInService,
            IOptions<IdentityOptions> identityOptions) =>
        {
            var user = await currentUser.GetAsync(context.User);
            if (user is null)
                return Results.Redirect("/login");

            // Refreshing re-issues the cookie from the store, so a disabled account must never reach
            // it - that would hand a fresh cookie to someone who has just had access taken away.
            if (!user.IsEnabled)
            {
                await signInService.SignOutAsync();
                return Results.Redirect("/login?error=session_ended");
            }

            // ...and neither must a cookie whose stamp has already been rotated. The stamp is only
            // re-read on an interval (5 min), so a revoked cookie still authenticates for that long -
            // long enough to reach this endpoint, which would re-issue it carrying the CURRENT stamp
            // and launder the revocation away. Polling here would then keep a signed-out session
            // alive indefinitely, defeating sign out everywhere and password change both. Same
            // question /api/account/revalidate asks, and it has to be asked here for the same reason.
            if (userManager.SupportsUserSecurityStamp)
            {
                var onCookie = context.User.FindFirstValue(
                    identityOptions.Value.ClaimsIdentity.SecurityStampClaimType);
                if (onCookie != await userManager.GetSecurityStampAsync(user))
                {
                    await signInService.SignOutAsync();
                    return Results.Redirect("/login?error=session_ended");
                }
            }

            await signInService.RefreshSignInAsync(user);

            // Local paths only: this endpoint is reachable with a crafted query, and an open redirect
            // off the back of a signed-in session is worth more to an attacker than the refresh is.
            return Results.LocalRedirect(SanitizeReturnUrl(context.Request.Query["returnUrl"].ToString()));
        })
            .RequireAuthorization(Policies.AccountSelfService);

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
    /// <summary>
    /// Announces a session revocation once this response has been written, rather than during it.
    ///
    /// Both callers rotate the security stamp and re-issue the caller's own cookie in the same
    /// response. The watcher then sends every circuit for the account to <c>/api/account/revalidate</c>
    /// - including the circuits of the browser that asked. Announcing inline means that browser is
    /// told to go and prove itself while the cookie proving it is still in flight, so it presents the
    /// one that was just invalidated and is signed out for changing its own password. Waiting for the
    /// response puts the new cookie in the browser's hands first.
    /// </summary>
    private static void RevokeAfterResponse(
        HttpContext context, UserSessionRevocationNotifier revocations, string userId)
    {
        // Read BEFORE the response is written: this is the id the asking browser's circuits are
        // holding, and it is what spares them. The replacement cookie carries a different one.
        var sparedSession = context.User.FindFirstValue(NetOptClaims.SessionId);

        context.Response.OnCompleted(() =>
        {
            revocations.NotifyRevoked(userId, sparedSession);
            return Task.CompletedTask;
        });
    }

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
    /// <summary>
    /// The only place a caller-supplied return URL becomes a redirect target. Anything that is not
    /// unambiguously a path on this site becomes "/".
    ///
    /// Backslashes are normalised BEFORE the checks. Browsers resolve <c>/\evil.example</c> and
    /// <c>\/evil.example</c> as scheme-relative, i.e. off-site, but neither starts with "//" nor
    /// contains ':' - so the original checks passed them straight through and an open redirect off a
    /// signed-in session is worth considerably more to an attacker than the redirect itself.
    /// </summary>
    private static string SanitizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl))
            return "/";

        var normalized = returnUrl.Replace('\\', '/');
        if (!normalized.StartsWith('/') ||
            normalized.StartsWith("//") ||
            normalized.Contains(':'))
        {
            return "/";
        }

        return normalized;
    }
}
