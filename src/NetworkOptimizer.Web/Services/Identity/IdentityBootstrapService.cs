using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Auditing;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Runs the one-time identity migration and keeps the local <c>admin</c> account in sync on every
/// boot: applies the <see cref="AuthDbContext"/> schema, ensures the global roles exist, and seeds
/// the <c>admin</c> user from whichever local credential the install currently uses so no one is
/// locked out by the JWT-to-cookie cutover (design doc 02, confirmed seed policy).
/// </summary>
public interface IIdentityBootstrapService
{
    /// <summary>Applies the auth schema, seeds roles, and reconciles the <c>admin</c> account.</summary>
    Task RunAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class IdentityBootstrapService : IIdentityBootstrapService
{
    /// <summary>The reserved local administrator username carried over from the single-password model.</summary>
    public const string AdminUserName = "admin";

    private readonly IDbContextFactory<AuthDbContext> _authDbFactory;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly IAuditLogger _audit;
    private readonly AdminAuthCache _adminAuthCache;
    private readonly ILogger<IdentityBootstrapService> _logger;

    public IdentityBootstrapService(
        IDbContextFactory<AuthDbContext> authDbFactory,
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        UserManager<ApplicationUser> userManager,
        RoleManager<ApplicationRole> roleManager,
        IAuditLogger audit,
        AdminAuthCache adminAuthCache,
        ILogger<IdentityBootstrapService> logger)
    {
        _authDbFactory = authDbFactory;
        _mainDbFactory = mainDbFactory;
        _userManager = userManager;
        _roleManager = roleManager;
        _audit = audit;
        _adminAuthCache = adminAuthCache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await using (var authDb = await _authDbFactory.CreateDbContextAsync(cancellationToken))
        {
            await authDb.Database.MigrateAsync(cancellationToken);
        }

        await EnsureRolesAsync();
        await ReconcileAdminAsync(cancellationToken);
    }

    /// <summary>Ensures the three global roles exist. Admin implies MFA-required is opt-in (default off).</summary>
    private async Task EnsureRolesAsync()
    {
        foreach (var roleName in Roles.All)
        {
            if (await _roleManager.RoleExistsAsync(roleName))
                continue;

            var role = new ApplicationRole(roleName)
            {
                Description = roleName switch
                {
                    Roles.Admin => "Full access: user, role, and federation management, licensing, all sites, audit log.",
                    Roles.Operator => "Operate permitted sites; no settings, user, or federation management.",
                    Roles.Viewer => "Read-only on permitted sites.",
                    _ => null,
                },
            };
            var result = await _roleManager.CreateAsync(role);
            if (!result.Succeeded)
                _logger.LogError("Failed to create role {Role}: {Errors}", roleName, Describe(result));
        }
    }

    /// <summary>
    /// Resolves the install's current local admin credential and creates or reconciles the
    /// <c>admin</c> user. Priority: <c>APP_PASSWORD</c> (a live per-boot override / reset path) wins,
    /// then a user-enabled DB password, then the auto-generated first-run password (kept flagged as
    /// temporary so the "set a real password" nag persists). If no local credential exists, the seed
    /// is skipped (e.g. an install using API-key auth, or before the first-run password is generated).
    /// </summary>
    private async Task ReconcileAdminAsync(CancellationToken cancellationToken)
    {
        var credential = await ResolveAdminCredentialAsync(cancellationToken);
        if (credential is null)
        {
            _logger.LogInformation("Identity bootstrap: no local admin credential present; skipping admin seed.");
            return;
        }

        var admin = await _userManager.FindByNameAsync(AdminUserName);
        if (admin is null)
        {
            await CreateAdminAsync(credential);
            return;
        }

        // A recovery boot re-enables the built-in admin. Disabling it is deliberately allowed (unlike
        // deleting it) on the stated grounds that break-glass recovers the account - but nothing
        // acted on that, so the documented way back in did not exist: with the account disabled every
        // sign-in route refuses it, recovery mode included, and an install whose only other admin had
        // been lost was unrecoverable. Setting the env var is an explicit, physical-access act by the
        // operator, which is exactly the authority this should take.
        if (BreakGlass.IsRecoveryMode && !admin.IsEnabled)
            await ReenableAdminForRecoveryAsync(admin);

        // Existing admin: re-sync only from a credential that is authoritative *this* boot, so a
        // changed env var takes effect on restart and a cleared password row still resets the login.
        // A transcoded DB/auto-gen hash that was merely read back is one-time and must not re-apply,
        // or every boot would overwrite a password since set through Identity.
        if (credential.Source is CredentialSource.Environment or CredentialSource.FirstRunReset)
            await ResyncPlaintextPasswordAsync(admin, credential);
    }

    private async Task CreateAdminAsync(AdminCredential credential)
    {
        var admin = new ApplicationUser
        {
            UserName = AdminUserName,
            DisplayName = "Administrator",
            IsEnabled = true,
            PasswordIsTemporary = credential.IsTemporary,
            SecurityStamp = Guid.NewGuid().ToString("N"),
        };

        IdentityResult result;
        if (credential.Plaintext is not null)
        {
            // Plaintext available (env var, or a password generated this boot) - let Identity
            // hash it at full strength.
            result = await _userManager.CreateAsync(admin, credential.Plaintext);
        }
        else
        {
            // DB / auto-generated: install the transcoded V3 hash directly, no plaintext needed.
            admin.PasswordHash = credential.PasswordHashV3;
            result = await _userManager.CreateAsync(admin);
        }

        if (!result.Succeeded)
        {
            _logger.LogError("Identity bootstrap: failed to create the admin user: {Errors}", Describe(result));
            return;
        }

        var roleResult = await _userManager.AddToRoleAsync(admin, Roles.Admin);
        if (!roleResult.Succeeded)
            _logger.LogError("Identity bootstrap: failed to grant admin the Admin role: {Errors}", Describe(roleResult));

        _logger.LogInformation(
            "Identity bootstrap: seeded local admin user (source={Source}, temporaryPassword={Temp}).",
            credential.Source, credential.IsTemporary);

        // The migration itself is an audited event (design doc 05).
        _audit.Log(AuditEventBuilder.From(
            CallerInfo.System("identity-migration"),
            AuditCategories.Audit, AuditActions.MigrationPerformed, AuditOutcomes.Success,
            targetType: "user", targetId: admin.Id, targetName: admin.UserName,
            details: new { source = credential.Source.ToString(), temporaryPassword = credential.IsTemporary }));
    }

    /// <summary>
    /// Re-enables the built-in admin on a recovery boot, loudly. This is the other half of the rule
    /// that lets the account be disabled but never deleted: deleting it is refused because it cannot
    /// be undone, and disabling it is allowed because this can.
    /// </summary>
    private async Task ReenableAdminForRecoveryAsync(ApplicationUser admin)
    {
        admin.IsEnabled = true;
        var result = await _userManager.UpdateAsync(admin);
        if (!result.Succeeded)
        {
            _logger.LogError(
                "Identity bootstrap: recovery mode could not re-enable the {Admin} account: {Errors}",
                AdminUserName, Describe(result));
            return;
        }

        _logger.LogWarning(
            "Identity bootstrap: recovery mode re-enabled the disabled {Admin} account for this boot.",
            AdminUserName);

        _audit.Log(AuditEventBuilder.From(
            CallerInfo.System("break-glass"),
            AuditCategories.Auth, AuditActions.BreakGlassUsed, AuditOutcomes.Success,
            targetType: "user", targetId: admin.Id, targetName: admin.UserName,
            details: new { reenabled = true }));
    }

    private async Task ResyncPlaintextPasswordAsync(ApplicationUser admin, AdminCredential credential)
    {
        var password = credential.Plaintext!;
        var fromEnv = credential.Source == CredentialSource.Environment;

        if (await _userManager.CheckPasswordAsync(admin, password))
            return; // already in sync

        var token = await _userManager.GeneratePasswordResetTokenAsync(admin);
        var result = await _userManager.ResetPasswordAsync(admin, token, password);
        if (!result.Succeeded)
        {
            _logger.LogError(
                "Identity bootstrap: failed to re-sync the admin password from {Source}: {Errors}",
                fromEnv ? "APP_PASSWORD" : "the regenerated first-run password", Describe(result));
            return;
        }

        admin.PasswordIsTemporary = credential.IsTemporary;

        if (!fromEnv)
        {
            // A reset is worth nothing if the account is then refused for some other reason, and an
            // operator who has lost the password has usually been failing sign-ins to find that out.
            admin.LockoutEnd = null;
            admin.AccessFailedCount = 0;
        }

        // ResetPasswordAsync has already rotated the security stamp, so existing sessions are gone.
        await _userManager.UpdateAsync(admin);

        if (fromEnv)
        {
            _logger.LogWarning(
                "Identity bootstrap: APP_PASSWORD differs from the stored admin hash; the env var wins " +
                "and the admin password was reset to it. Unset APP_PASSWORD to manage the password in-app.");
            return;
        }

        _logger.LogWarning(
            "Identity bootstrap: the stored admin password was cleared, so the regenerated first-run " +
            "password above was applied to the admin account and its lockout was cleared.");

        // Only the reset path is audited. The env var re-syncing on boot is existing behaviour and
        // stays silent; auditing it here would be an unrelated change to a path nobody asked about.
        _audit.Log(AuditEventBuilder.From(
            CallerInfo.System("password-reset"),
            AuditCategories.Auth, AuditActions.PasswordReset, AuditOutcomes.Success,
            targetType: "user", targetId: admin.Id, targetName: admin.UserName,
            details: new { source = credential.Source.ToString() }));
    }

    /// <summary>Resolves the effective local admin credential, or null when there is none to seed.</summary>
    private async Task<AdminCredential?> ResolveAdminCredentialAsync(CancellationToken cancellationToken)
    {
        // APP_PASSWORD is a live per-boot override and the homelab/Docker reset path: it wins.
        var envPassword = Environment.GetEnvironmentVariable("APP_PASSWORD");
        if (!string.IsNullOrEmpty(envPassword))
            return AdminCredential.FromEnvironment(envPassword);

        // A password generated moments ago by AdminAuthService means the stored one was absent:
        // a first run, or scripts/reset-password.* having cleared it. That password has just been
        // printed to the log as the way back in, so it outranks the hash now sitting in the row.
        var firstRunPassword = _adminAuthCache.ConsumeFirstRunPassword();
        if (!string.IsNullOrEmpty(firstRunPassword))
            return AdminCredential.FromFirstRunReset(firstRunPassword);

        await using var mainDb = await _mainDbFactory.CreateDbContextAsync(cancellationToken);
        var settings = await mainDb.AdminSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (settings?.Password is null || !LegacyPasswordTranscoder.IsLegacyFormat(settings.Password))
            return null;

        var v3 = LegacyPasswordTranscoder.TranscodeToIdentityV3(settings.Password);

        // Enabled == user chose this password (not temporary); otherwise it is the auto-generated
        // first-run password and stays flagged temporary so the nag persists.
        return AdminCredential.FromLegacyHash(v3, isTemporary: !settings.Enabled);
    }

    private static string Describe(IdentityResult result)
        => string.Join("; ", result.Errors.Select(e => $"{e.Code}:{e.Description}"));

    private enum CredentialSource
    {
        Environment,
        LegacyHash,
        FirstRunReset,
    }

    private sealed record AdminCredential(
        CredentialSource Source, string? Plaintext, string? PasswordHashV3, bool IsTemporary)
    {
        public static AdminCredential FromEnvironment(string plaintext)
            => new(CredentialSource.Environment, plaintext, null, IsTemporary: false);

        public static AdminCredential FromLegacyHash(string v3Hash, bool isTemporary)
            => new(CredentialSource.LegacyHash, null, v3Hash, isTemporary);

        /// <summary>A password auto-generated this boot because none was stored; always temporary.</summary>
        public static AdminCredential FromFirstRunReset(string plaintext)
            => new(CredentialSource.FirstRunReset, plaintext, null, IsTemporary: true);
    }
}
