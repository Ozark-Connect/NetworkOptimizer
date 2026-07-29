using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Auditing;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>A registered passkey credential, projected for display/management.</summary>
public sealed record PasskeySummary(string CredentialId, string? Name, DateTimeOffset CreatedAt);

/// <summary>
/// WebAuthn passkeys over .NET 10's built-in ASP.NET Core Identity passkey support (design doc 02):
/// registration (attestation) and assertion, multiple named credentials per user, individually
/// revocable, usable as a second factor and as primary passwordless login. Registration/removal are
/// audited. Callers must first check <see cref="SecureContext.IsSecure"/> - the ceremonies only work
/// in a secure context.
/// </summary>
public interface IPasskeyService
{
    /// <summary>Lists the user's registered passkeys.</summary>
    Task<IReadOnlyList<PasskeySummary>> ListAsync(ClaimsPrincipal principal);

    /// <summary>Builds the WebAuthn creation options JSON for <c>navigator.credentials.create</c>.</summary>
    Task<string> CreationOptionsAsync(ClaimsPrincipal principal, HttpContext http);

    /// <summary>Completes registration from the browser's attestation response; returns the stored credential name.</summary>
    Task<bool> CompleteRegistrationAsync(ClaimsPrincipal principal, string credentialJson, string? name);

    /// <summary>Builds the WebAuthn request options JSON for <c>navigator.credentials.get</c> (usernameless when user is null).</summary>
    Task<string> RequestOptionsAsync(ApplicationUser? user, HttpContext http);

    /// <summary>Removes a passkey by credential id (audited).</summary>
    Task RemoveAsync(ClaimsPrincipal principal, string credentialId);

    /// <summary>Renames a passkey (audited).</summary>
    Task RenameAsync(ClaimsPrincipal principal, string credentialId, string name);
}

/// <inheritdoc />
public sealed class PasskeyService : IPasskeyService
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IDbContextFactory<AuthDbContext> _authDbFactory;
    private readonly IAuditLogger _audit;
    private readonly ICallerContext _caller;

    public PasskeyService(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        IDbContextFactory<AuthDbContext> authDbFactory,
        IAuditLogger audit,
        ICallerContext caller)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _authDbFactory = authDbFactory;
        _audit = audit;
        _caller = caller;
    }

    public async Task<IReadOnlyList<PasskeySummary>> ListAsync(ClaimsPrincipal principal)
    {
        var user = await RequireUserAsync(principal);
        await using var db = await _authDbFactory.CreateDbContextAsync();
        var rows = await db.Passkeys
            .AsNoTracking()
            .Where(p => p.UserId == user.Id)
            .ToListAsync();
        return rows
            .Select(p => new PasskeySummary(Convert.ToBase64String(p.CredentialId), p.Data.Name, p.Data.CreatedAt))
            .ToList();
    }

    public async Task<string> CreationOptionsAsync(ClaimsPrincipal principal, HttpContext http)
    {
        var user = await RequireUserAsync(principal);
        var userEntity = new PasskeyUserEntity
        {
            Id = user.Id,
            Name = user.UserName ?? user.Id,
            DisplayName = user.DisplayName ?? user.UserName ?? user.Id,
        };
        return await _signInManager.MakePasskeyCreationOptionsAsync(userEntity);
    }

    public async Task<bool> CompleteRegistrationAsync(ClaimsPrincipal principal, string credentialJson, string? name)
    {
        var user = await RequireUserAsync(principal);
        var attestation = await _signInManager.PerformPasskeyAttestationAsync(credentialJson);
        if (!attestation.Succeeded)
            return false;

        var result = await _userManager.AddOrUpdatePasskeyAsync(user, attestation.Passkey);
        if (!result.Succeeded)
            return false;

        if (!string.IsNullOrEmpty(name))
            await RenameAsync(principal, Convert.ToBase64String(attestation.Passkey.CredentialId), name);

        Emit(AuditActions.PasskeyRegistered, user, name);
        return true;
    }

    public async Task<string> RequestOptionsAsync(ApplicationUser? user, HttpContext http)
        => await _signInManager.MakePasskeyRequestOptionsAsync(user);

    public async Task RemoveAsync(ClaimsPrincipal principal, string credentialId)
    {
        var user = await RequireUserAsync(principal);
        var id = Convert.FromBase64String(credentialId);
        await _userManager.RemovePasskeyAsync(user, id);
        Emit(AuditActions.PasskeyRemoved, user, credentialId);
    }

    public async Task RenameAsync(ClaimsPrincipal principal, string credentialId, string name)
    {
        var user = await RequireUserAsync(principal);
        await using var db = await _authDbFactory.CreateDbContextAsync();
        var id = Convert.FromBase64String(credentialId);
        var passkey = await db.Passkeys.FirstOrDefaultAsync(p => p.UserId == user.Id && p.CredentialId == id);
        if (passkey is not null)
        {
            passkey.Data.Name = name;
            await db.SaveChangesAsync();
            Emit(AuditActions.PasskeyRenamed, user, name);
        }
    }

    private async Task<ApplicationUser> RequireUserAsync(ClaimsPrincipal principal)
        => await _userManager.GetUserAsync(principal)
            ?? throw new InvalidOperationException("No authenticated user for the passkey operation.");

    private void Emit(string action, ApplicationUser user, string? detail)
        => _audit.Log(AuditEventBuilder.From(_caller.Current, AuditCategories.Auth, action,
            targetType: "user", targetId: user.Id, targetName: user.UserName,
            details: detail is null ? null : new { name = detail }));
}
