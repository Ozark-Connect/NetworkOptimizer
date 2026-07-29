using Microsoft.AspNetCore.Identity;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Auditing;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Transitional middleware that upgrades a still-valid legacy <c>auth_token</c> JWT into an Identity
/// application cookie, so sessions established before the identity migration survive the JWT-to-cookie
/// cutover without a forced re-login (design doc 02, migration step 4). A legacy token maps
/// unambiguously to the single possible principal - the migrated <c>admin</c> user.
/// </summary>
/// <remarks>
/// SUNSET: remove this middleware, <see cref="JwtService"/>, and the persisted signing key one release
/// after the cutover ships - the legacy tokens have a 30-day lifetime, so a single release cycle fully
/// covers their expiry. This is a complete shipped feature, not a placeholder; the removal condition is
/// documented here and in the PR body rather than tracked as a TODO.
/// </remarks>
public sealed class LegacyJwtBridgeMiddleware
{
    private const string LegacyCookieName = "auth_token";

    private readonly RequestDelegate _next;
    private readonly ILogger<LegacyJwtBridgeMiddleware> _logger;

    public LegacyJwtBridgeMiddleware(RequestDelegate next, ILogger<LegacyJwtBridgeMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IJwtService jwtService,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IAuditLogger audit)
    {
        // Already authenticated via the Identity cookie, or no legacy token to exchange: nothing to do.
        if (context.User.Identity?.IsAuthenticated == true ||
            !context.Request.Cookies.TryGetValue(LegacyCookieName, out var token) ||
            string.IsNullOrEmpty(token))
        {
            await _next(context);
            return;
        }

        var principal = await jwtService.ValidateTokenAsync(token);
        if (principal is not null)
        {
            var admin = await userManager.FindByNameAsync(IdentityBootstrapService.AdminUserName);
            if (admin is { IsEnabled: true })
            {
                // Issue the Identity cookie for subsequent requests. Persistent, matching the
                // Keep me signed in option: the legacy token being exchanged carried up to 30 days
                // of life, so a session-scoped cookie would silently shorten a session the user
                // already had. Fourteen days sliding is the longest the application cookie offers.
                await signInManager.SignInAsync(admin, isPersistent: true);
                // ...and populate this request's principal so the current request is authenticated.
                context.User = await signInManager.CreateUserPrincipalAsync(admin);
                DeleteLegacyCookie(context);

                _logger.LogInformation(
                    "Session bridge: exchanged a legacy auth_token JWT for an Identity cookie for the admin account.");
                audit.Log(AuditEventBuilder.From(
                    CallerInfo.ForUser(context.User,
                        context.Connection.RemoteIpAddress?.ToString(),
                        context.Request.Headers.UserAgent.ToString(),
                        context.TraceIdentifier),
                    AuditCategories.Auth, AuditActions.BridgeExchange, AuditOutcomes.Success,
                    targetType: "user", targetId: admin.Id, targetName: admin.UserName));
            }
            else
            {
                // Valid token but the admin is gone/disabled - drop the stale cookie.
                DeleteLegacyCookie(context);
            }
        }

        await _next(context);
    }

    private static void DeleteLegacyCookie(HttpContext context)
        => context.Response.Cookies.Delete(LegacyCookieName, new CookieOptions { Path = "/" });
}
