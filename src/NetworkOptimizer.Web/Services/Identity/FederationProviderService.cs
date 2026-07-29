using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Auditing;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Manages configured federation providers (design doc 03). Secrets (OIDC client secret, SAML
/// decryption cert) are encrypted at rest with Data Protection, are write-only in the UI (never
/// returned after save), and are never logged. Provider changes are audited.
/// </summary>
public interface IFederationProviderService
{
    Task<IReadOnlyList<FederationProvider>> GetAllAsync();
    Task<IReadOnlyList<FederationProvider>> GetEnabledAsync();
    Task<FederationProvider?> GetBySchemeAsync(string scheme);

    /// <summary>Decrypts a stored client secret for handler configuration (never exposed to the UI).</summary>
    string? UnprotectClientSecret(FederationProvider provider);

    // Save/SetEnabled/Delete are NOT here. The sign-in page reads this interface anonymously to draw
    // its provider buttons, and the OIDC and SAML handlers read it while configuring themselves with
    // no caller established, so it cannot be gated. Leaving the mutations on it meant anything holding
    // it could register an identity provider - and an attacker-chosen IdP with JIT provisioning and
    // role mapping signs in as whoever it likes. They live on IFederationAdminService below.
}

/// <summary>
/// Adding, changing and removing identity providers (design doc 06, gate 9).
///
/// Global Admin without exception: a provider row decides who may authenticate to this instance and
/// what roles they arrive holding, so writing one is at least as powerful as editing accounts
/// directly. Separate from <see cref="IFederationProviderService"/> because that has to answer
/// anonymous callers - see the note there.
/// </summary>
[MutatingService]
public interface IFederationAdminService
{
    [RequireRole(Roles.Admin)]
    Task<int> SaveAsync(FederationProvider provider, string? newClientSecret);

    [RequireRole(Roles.Admin)]
    Task SetEnabledAsync(int id, bool enabled);

    [RequireRole(Roles.Admin)]
    Task DeleteAsync(int id);
}

/// <inheritdoc />
public sealed class FederationProviderService : IFederationProviderService, IFederationAdminService
{
    private readonly IDbContextFactory<AuthDbContext> _dbFactory;
    /// <summary>
    /// The same credential protection the SSH passwords, console password and notification-channel
    /// secrets use. Deliberately NOT raw Data Protection with its own purpose string: that is a
    /// second key store, so a restore needs both key files and getting one without the other leaves
    /// half the product's secrets readable and half not - which reads as corruption rather than as a
    /// missing file.
    /// </summary>
    private readonly NetworkOptimizer.Storage.Services.ICredentialProtectionService _secrets;
    private readonly IAuditLogger _audit;
    private readonly ICallerContext _caller;
    private readonly DynamicSchemeManager _schemes;
    private readonly ILogger<FederationProviderService> _logger;

    public FederationProviderService(
        IDbContextFactory<AuthDbContext> dbFactory,
        NetworkOptimizer.Storage.Services.ICredentialProtectionService secrets,
        IAuditLogger audit,
        ICallerContext caller,
        DynamicSchemeManager schemes,
        ILogger<FederationProviderService> logger)
    {
        _dbFactory = dbFactory;
        _secrets = secrets;
        _audit = audit;
        _caller = caller;
        _schemes = schemes;
        _logger = logger;
    }

    public async Task<IReadOnlyList<FederationProvider>> GetAllAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await Include(db.FederationProviders).OrderBy(p => p.SortOrder).ToListAsync();
    }

    public async Task<IReadOnlyList<FederationProvider>> GetEnabledAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await Include(db.FederationProviders).Where(p => p.Enabled).OrderBy(p => p.SortOrder).ToListAsync();
    }

    public async Task<FederationProvider?> GetBySchemeAsync(string scheme)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await Include(db.FederationProviders).FirstOrDefaultAsync(p => p.Scheme == scheme);
    }

    public async Task<int> SaveAsync(FederationProvider provider, string? newClientSecret)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var isNew = provider.Id == 0;

        if (!string.IsNullOrEmpty(newClientSecret))
            provider.ClientSecretProtected = _secrets.Encrypt(newClientSecret);

        if (isNew)
        {
            provider.CreatedAt = DateTime.UtcNow;
            provider.UpdatedAt = DateTime.UtcNow;
            db.FederationProviders.Add(provider);
        }
        else
        {
            var existing = await Include(db.FederationProviders).FirstAsync(p => p.Id == provider.Id);
            // Preserve the stored secret when the write-only field is left blank.
            if (string.IsNullOrEmpty(newClientSecret))
                provider.ClientSecretProtected = existing.ClientSecretProtected;
            db.Entry(existing).CurrentValues.SetValues(provider);
            existing.UpdatedAt = DateTime.UtcNow;
            await SyncChildrenAsync(db, existing, provider);
        }

        await db.SaveChangesAsync();

        _audit.Log(AuditEventBuilder.From(_caller.Current, AuditCategories.Federation,
            isNew ? AuditActions.ProviderCreated : AuditActions.ProviderUpdated,
            targetType: "provider", targetId: provider.Scheme, targetName: provider.DisplayName));
        await _schemes.SyncAsync(); // register/refresh the scheme at runtime (no restart)
        return provider.Id;
    }

    public async Task SetEnabledAsync(int id, bool enabled)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var provider = await db.FederationProviders.FindAsync(id);
        if (provider is null) return;
        provider.Enabled = enabled;
        provider.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        _audit.Log(AuditEventBuilder.From(_caller.Current, AuditCategories.Federation,
            enabled ? AuditActions.ProviderEnabled : AuditActions.ProviderDisabled,
            targetType: "provider", targetId: provider.Scheme, targetName: provider.DisplayName));

        // The scheme registry follows the toggle, exactly as it follows a save or a delete. Without
        // this, enabling a provider that was created disabled leaves it with no registered scheme -
        // the challenge then throws "No authentication handler is registered" until the app restarts.
        // Disabling had the mirror problem: the scheme stayed live and kept accepting sign-ins.
        if (enabled)
            await _schemes.SyncAsync();
        else
            await _schemes.RemoveAsync(provider.Scheme);
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var provider = await db.FederationProviders.FindAsync(id);
        if (provider is null) return;
        db.FederationProviders.Remove(provider);
        await db.SaveChangesAsync();
        await _schemes.RemoveAsync(provider.Scheme);
        _audit.Log(AuditEventBuilder.From(_caller.Current, AuditCategories.Federation, AuditActions.ProviderUpdated,
            targetType: "provider", targetId: provider.Scheme, targetName: provider.DisplayName,
            details: new { deleted = true }));
    }

    public string? UnprotectClientSecret(FederationProvider provider)
    {
        if (string.IsNullOrEmpty(provider.ClientSecretProtected)) return null;
        try { return _secrets.Decrypt(provider.ClientSecretProtected); }
        catch (Exception ex)
        {
            // Logged, because a lost or rotated data-protection key produces exactly the same null as
            // "no secret configured" - and the admin then sees a federation login fail with nothing
            // anywhere pointing at the real cause.
            _logger.LogWarning(ex, "Stored client secret could not be unprotected; treating it as unset.");
            return null;
        }
    }

    private static IQueryable<FederationProvider> Include(IQueryable<FederationProvider> q)
        => q.Include(p => p.RoleMappings).Include(p => p.SiteMappings);

    private static async Task SyncChildrenAsync(AuthDbContext db, FederationProvider existing, FederationProvider incoming)
    {
        await db.FederationRoleMappings.Where(m => m.ProviderId == existing.Id).ExecuteDeleteAsync();
        await db.FederationSiteMappings.Where(m => m.ProviderId == existing.Id).ExecuteDeleteAsync();
        foreach (var m in incoming.RoleMappings)
            db.FederationRoleMappings.Add(new FederationRoleMapping { ProviderId = existing.Id, GroupOrClaimValue = m.GroupOrClaimValue, GlobalRole = m.GlobalRole });
        foreach (var m in incoming.SiteMappings)
            db.FederationSiteMappings.Add(new FederationSiteMapping { ProviderId = existing.Id, GroupOrClaimValue = m.GroupOrClaimValue, TargetType = m.TargetType, TargetValue = m.TargetValue, SiteRole = m.SiteRole });
    }
}
