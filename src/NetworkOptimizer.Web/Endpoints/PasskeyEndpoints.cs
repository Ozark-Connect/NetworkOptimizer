using Microsoft.AspNetCore.Identity;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Identity;

namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// WebAuthn passkey ceremony endpoints (design doc 02). Registration endpoints require an
/// authenticated session and run through <see cref="IPasskeyService"/>; the assertion (passwordless
/// login) endpoints are anonymous and complete via <see cref="SignInManager{TUser}"/>. Ceremonies
/// only work in a secure context - the UI gates on <see cref="SecureContext.IsSecure"/> before calling.
/// </summary>
public static class PasskeyEndpoints
{
    public static void MapPasskeyEndpoints(this WebApplication app)
    {
        var authed = app.MapGroup("/api/passkey").RequireAuthorization();

        authed.MapPost("/creation-options", async (HttpContext ctx, IPasskeyService passkeys) =>
        {
            if (!SecureContext.IsSecure(ctx)) return Results.BadRequest(new { error = "insecure_context" });
            var json = await passkeys.CreationOptionsAsync(ctx.User, ctx);
            return Results.Content(json, "application/json");
        });

        authed.MapPost("/register", async (HttpContext ctx, IPasskeyService passkeys) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<PasskeyRegisterRequest>();
            if (body is null || string.IsNullOrEmpty(body.Credential))
                return Results.BadRequest(new { error = "missing_credential" });
            var ok = await passkeys.CompleteRegistrationAsync(ctx.User, body.Credential, body.Name);
            return ok ? Results.Ok(new { registered = true }) : Results.BadRequest(new { error = "attestation_failed" });
        });

        authed.MapPost("/remove", async (HttpContext ctx, IPasskeyService passkeys, string id) =>
        {
            await passkeys.RemoveAsync(ctx.User, id);
            return Results.Ok();
        });

        // --- anonymous passwordless login ---
        app.MapGet("/api/passkey/request-options", async (HttpContext ctx, IPasskeyService passkeys) =>
        {
            if (!SecureContext.IsSecure(ctx)) return Results.BadRequest(new { error = "insecure_context" });
            var json = await passkeys.RequestOptionsAsync(user: null, ctx);
            return Results.Content(json, "application/json");
        }).AllowAnonymous();

        app.MapPost("/api/passkey/assert", async (
            HttpContext ctx,
            SignInManager<ApplicationUser> signInManager,
            IIdentitySignInService signInService) =>
        {
            var credentialJson = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            var assertion = await signInManager.PerformPasskeyAssertionAsync(credentialJson);
            if (!assertion.Succeeded)
                return Results.BadRequest(new { error = "assertion_failed" });

            // The sign-in itself goes through the shared service, so a passkey login is gated and
            // audited exactly like a password one. A refusal reports the same way as a failed
            // assertion, so a disabled account is not distinguishable from a bad credential.
            var outcome = await signInService.PasskeySignInAsync(assertion.User);
            return outcome == SignInOutcome.Success
                ? Results.Ok(new { authenticated = true })
                : Results.BadRequest(new { error = "assertion_failed" });
        }).AllowAnonymous().RequireRateLimiting("Authentication");
    }

    private sealed record PasskeyRegisterRequest(string Credential, string? Name);
}
