using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Auditing;
using NetworkOptimizer.Web.Services.Identity;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

/// <summary>
/// Moving a user between global roles is ONE service call, so the permissions signal that reaches
/// their live sessions fires once, after the whole change has landed. It used to be a grant call plus
/// a revoke call, and the first signal reloads the target's tab - which lost the second and left the
/// cookie holding the state between them.
///
/// These run against a real <see cref="UserManager{TUser}"/> and SQLite database, because what is
/// being pinned is the end state in the role store: exactly one role afterwards, and the two refusals
/// that stop an install being locked out of its own admin account. Those invariants moved when the
/// call was combined - they are asked up front now rather than after the grant - so they are the part
/// worth holding still.
/// </summary>
public sealed class GlobalRoleChangeTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"netopt-role-test-{Guid.NewGuid():N}.db");

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();

        var mainOptions = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        services.AddSingleton<IDbContextFactory<NetworkOptimizerDbContext>>(
            new NetworkOptimizerDbContextFactory(mainOptions));

        services.AddNetOptIdentityCore(_dbPath);
        services.AddSingleton<IAuditLogger>(new NoOpAuditLogger());
        services.AddScoped<ICallerContext, CallerContext>();
        services.AddScoped<NetworkOptimizer.Web.Services.Authorization.IEffectiveSiteRoleResolver, UnusedSiteRoleResolver>();
        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<AdminAuthCache>();
        services.AddScoped<IAdminAuthService, AdminAuthService>();
        services.AddGatePlumbing();
        return services.BuildServiceProvider();
    }

    private async Task<ServiceProvider> BootedProviderAsync()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        await using (var db = new NetworkOptimizerDbContext(options))
        {
            await db.Database.MigrateAsync();
            db.AdminSettings.Add(new AdminSettings
            {
                Password = new PasswordHasher().HashPassword("Seed-Password-1"),
                Enabled = true,
            });
            await db.SaveChangesAsync();
        }

        var provider = BuildProvider();
        using var boot = provider.CreateScope();
        await boot.ServiceProvider.GetRequiredService<IIdentityBootstrapService>().RunAsync();
        return provider;
    }

    private static async Task<string> SeedUserAsync(ServiceProvider provider, string name, string role)
    {
        using var scope = provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = name, IsEnabled = true };
        (await userManager.CreateAsync(user, "Seeded-Password-9")).Succeeded.Should().BeTrue();
        (await userManager.AddToRoleAsync(user, role)).Succeeded.Should().BeTrue();
        return user.Id;
    }

    private static async Task<IList<string>> RolesOfAsync(ServiceProvider provider, string userId)
    {
        using var scope = provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(userId);
        return await userManager.GetRolesAsync(user!);
    }

    /// <summary>The built-in admin, so an install always has an Admin other than the user under test.</summary>
    private static async Task<string> BuiltInAdminIdAsync(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var admin = await userManager.FindByNameAsync(IdentityBootstrapService.AdminUserName);
        return admin!.Id;
    }

    [Fact]
    public async Task Demotion_LeavesExactlyTheNewRole()
    {
        await using var provider = await BootedProviderAsync();
        var actingAdmin = await BuiltInAdminIdAsync(provider);
        var target = await SeedUserAsync(provider, "demote.me", Roles.Admin);

        using (var scope = provider.ScopeAs(actingAdmin, Roles.Admin))
        {
            var identityAdmin = scope.ServiceProvider.GetRequiredService<IIdentityAdminService>();
            var result = await identityAdmin.SetGlobalRoleAsync(target, Roles.Viewer);
            result.Succeeded.Should().BeTrue(result.Error);
        }

        (await RolesOfAsync(provider, target)).Should().BeEquivalentTo(new[] { Roles.Viewer },
            "the account must not be left holding the role it was moved off");
    }

    [Fact]
    public async Task Promotion_LeavesExactlyTheNewRole()
    {
        await using var provider = await BootedProviderAsync();
        var actingAdmin = await BuiltInAdminIdAsync(provider);
        var target = await SeedUserAsync(provider, "promote.me", Roles.Viewer);

        using (var scope = provider.ScopeAs(actingAdmin, Roles.Admin))
        {
            var identityAdmin = scope.ServiceProvider.GetRequiredService<IIdentityAdminService>();
            var result = await identityAdmin.SetGlobalRoleAsync(target, Roles.Admin);
            result.Succeeded.Should().BeTrue(result.Error);
        }

        (await RolesOfAsync(provider, target)).Should().BeEquivalentTo(new[] { Roles.Admin });
    }

    [Fact]
    public async Task ADeliberateNoOp_KeepsTheRole()
    {
        await using var provider = await BootedProviderAsync();
        var actingAdmin = await BuiltInAdminIdAsync(provider);
        var target = await SeedUserAsync(provider, "unchanged", Roles.Operator);

        using (var scope = provider.ScopeAs(actingAdmin, Roles.Admin))
        {
            var identityAdmin = scope.ServiceProvider.GetRequiredService<IIdentityAdminService>();
            (await identityAdmin.SetGlobalRoleAsync(target, Roles.Operator)).Succeeded.Should().BeTrue();
        }

        (await RolesOfAsync(provider, target)).Should().BeEquivalentTo(new[] { Roles.Operator });
    }

    [Fact]
    public async Task YouCannotDemoteYourself()
    {
        await using var provider = await BootedProviderAsync();
        var self = await BuiltInAdminIdAsync(provider);
        // A second Admin, so the refusal under test is the self rule and not the last-admin rule.
        await SeedUserAsync(provider, "other.admin", Roles.Admin);

        using (var scope = provider.ScopeAs(self, Roles.Admin))
        {
            var identityAdmin = scope.ServiceProvider.GetRequiredService<IIdentityAdminService>();
            var result = await identityAdmin.SetGlobalRoleAsync(self, Roles.Viewer);
            result.Succeeded.Should().BeFalse("removing your own Admin role is refused");
            result.Error.Should().Contain("your own Admin role",
                "the refusal must be the self rule, not some other failure passing for it");
        }

        (await RolesOfAsync(provider, self)).Should().Contain(Roles.Admin,
            "a refusal must leave the role exactly as it was");
    }

    [Fact]
    public async Task TheLastAdminCannotBeDemoted()
    {
        await using var provider = await BootedProviderAsync();
        var lastAdmin = await BuiltInAdminIdAsync(provider);

        // The caller holds Admin on the PRINCIPAL but not in the store, so the role gate lets the call
        // through while the install still has exactly one Admin to lose. Acting as a real second Admin
        // would mean there was no last admin to protect, and the test would pass without the rule.
        var mover = await SeedUserAsync(provider, "mover", Roles.Viewer);

        using (var scope = provider.ScopeAs(mover, Roles.Admin))
        {
            var identityAdmin = scope.ServiceProvider.GetRequiredService<IIdentityAdminService>();
            var result = await identityAdmin.SetGlobalRoleAsync(lastAdmin, Roles.Viewer);
            result.Succeeded.Should().BeFalse("demoting the only enabled Admin locks the install out");
            result.Error.Should().Contain("last enabled administrator",
                "the refusal must be the last-admin rule, not some other failure passing for it");
        }

        (await RolesOfAsync(provider, lastAdmin)).Should().Contain(Roles.Admin,
            "a refusal must leave the role exactly as it was");
    }

    /// <summary>Global roles are the subject here; nothing resolves a site role.</summary>
    private sealed class UnusedSiteRoleResolver : NetworkOptimizer.Web.Services.Authorization.IEffectiveSiteRoleResolver
    {
        public void Invalidate(string userId) { }

        public void InvalidateAll() { }

        public Task<string?> FirstAdministeredSlugAsync(System.Security.Claims.ClaimsPrincipal user)
            => Task.FromResult<string?>(null);

        public Task<SiteRole?> GetEffectiveRoleAsync(System.Security.Claims.ClaimsPrincipal user, string slug)
            => Task.FromResult<SiteRole?>(null);

        public Task<IReadOnlySet<string>> GetAuthorizedSlugsAsync(System.Security.Claims.ClaimsPrincipal user)
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    }

    private sealed class NoOpAuditLogger : IAuditLogger
    {
        public void Log(AuditEvent auditEvent) { }
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
        catch (IOException) { /* temp file, ignored */ }
    }
}
