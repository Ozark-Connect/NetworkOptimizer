using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Auditing;
using NetworkOptimizer.Web.Services.Authorization;
using NetworkOptimizer.Web.Services.Identity;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

/// <summary>
/// The site-ownership invariant on membership changes. A SiteAdmin administers exactly one site, and
/// the scoped Access card narrows the target in the UI - but a Blazor circuit's bound values arrive
/// from the browser, so the service is what has to refuse. Without this a SiteAdmin of one site could
/// grant themselves AllSites, or edit access on somebody else's site.
/// </summary>
public sealed class MembershipOwnershipTests : IDisposable
{
    private const string OwnedSite = "site-a";
    private const string OtherSite = "site-b";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"netopt-own-test-{Guid.NewGuid():N}.db");

    /// <summary>Reports SiteAdmin on exactly one site, which is what the guard consults.</summary>
    private sealed class SiteAdminOf : IEffectiveSiteRoleResolver
    {
        public void Invalidate(string userId) { }

        public void InvalidateAll() { }

        public Task<string?> FirstAdministeredSlugAsync(System.Security.Claims.ClaimsPrincipal user)
            => Task.FromResult<string?>(null);

        private readonly string _slug;
        public SiteAdminOf(string slug) => _slug = slug;

        // A global Admin administers every site, exactly as EffectiveSiteRoleResolver has it - without
        // that, a site-scoped gate refuses the global Admin the test is acting as.
        public Task<SiteRole?> GetEffectiveRoleAsync(ClaimsPrincipal user, string slug) =>
            Task.FromResult<SiteRole?>(
                user.IsInRole(Roles.Admin) || string.Equals(slug, _slug, StringComparison.OrdinalIgnoreCase)
                    ? SiteRole.SiteAdmin
                    : null);

        public Task<IReadOnlySet<string>> GetAuthorizedSlugsAsync(ClaimsPrincipal user) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string> { _slug });
    }

    private sealed class NoOpAuditLogger : IAuditLogger
    {
        public void Log(AuditEvent auditEvent) { }
    }

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
        services.AddScoped<IEffectiveSiteRoleResolver>(_ => new SiteAdminOf(OwnedSite));
        services.AddGatePlumbing(OwnedSite);
        return services.BuildServiceProvider();
    }

    /// <summary>A site admin: authenticated, holds no global Admin role.</summary>
    private static ClaimsPrincipal SiteAdminPrincipal() => new(new ClaimsIdentity(
        new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "site-admin-1"),
            new Claim(ClaimTypes.Name, "siteadmin1"),
            new Claim(ClaimTypes.Role, Roles.Viewer),
        },
        "test"));

    private static ClaimsPrincipal GlobalAdminPrincipal() => new(new ClaimsIdentity(
        new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "admin-1"),
            new Claim(ClaimTypes.Name, "admin"),
            new Claim(ClaimTypes.Role, Roles.Admin),
        },
        "test"));

    private static async Task<string> SeedTargetUserAsync(IServiceProvider sp)
    {
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var target = new ApplicationUser { UserName = "tech1", IsEnabled = true };
        await users.CreateAsync(target);
        await users.AddToRoleAsync(target, Roles.Viewer);
        return target.Id;
    }

    private async Task<(ServiceProvider Root, IServiceScope Scope, IIdentityAdminService Admin, string TargetId)>
        ActingAsAsync(ClaimsPrincipal principal)
    {
        var provider = BuildProvider();
        using (var seed = provider.CreateScope())
        {
            await seed.ServiceProvider.GetRequiredService<AuthDbContext>().Database.MigrateAsync();

            var roles = seed.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            foreach (var role in Roles.All)
                await roles.CreateAsync(new ApplicationRole { Name = role });
        }

        var scope = provider.CreateScope();
        var targetId = await SeedTargetUserAsync(scope.ServiceProvider);
        scope.ServiceProvider.GetRequiredService<ICallerContext>()
            .SetUser(CallerInfo.ForUser(principal, "203.0.113.5", "test-agent", "corr-1"));
        return (provider, scope, scope.ServiceProvider.GetRequiredService<IIdentityAdminService>(), targetId);
    }

    [Fact]
    public async Task ASiteAdminCanGrantOnTheSiteTheyAdminister()
    {
        var (root, scope, admin, targetId) = await ActingAsAsync(SiteAdminPrincipal());
        using (root) using (scope)
        {
            var result = await admin.AddMembershipAsync(
                targetId, MembershipTargetType.Site, OwnedSite, SiteRole.SiteViewer);

            result.Succeeded.Should().BeTrue(result.Error);
        }
    }

    [Fact]
    public async Task ASiteAdminCannotGrantOnAnotherSite()
    {
        var (root, scope, admin, targetId) = await ActingAsAsync(SiteAdminPrincipal());
        using (root) using (scope)
        {
            var result = await admin.AddMembershipAsync(
                targetId, MembershipTargetType.Site, OtherSite, SiteRole.SiteViewer);

            result.Succeeded.Should().BeFalse("the UI scopes the target, so only the service can refuse it");
        }
    }

    [Fact]
    public async Task ASiteAdminCannotGrantAllSites()
    {
        var (root, scope, admin, targetId) = await ActingAsAsync(SiteAdminPrincipal());
        using (root) using (scope)
        {
            var result = await admin.AddMembershipAsync(
                targetId, MembershipTargetType.AllSites, null, SiteRole.SiteAdmin);

            result.Succeeded.Should().BeFalse("AllSites reaches every site, so it is an Admin-only grant");
        }
    }

    [Fact]
    public async Task ASiteAdminCannotGrantThroughAGroup()
    {
        var (root, scope, admin, targetId) = await ActingAsAsync(SiteAdminPrincipal());
        using (root) using (scope)
        {
            var result = await admin.AddMembershipAsync(
                targetId, MembershipTargetType.Group, "group-1", SiteRole.SiteOperator);

            result.Succeeded.Should().BeFalse("a group spans sites the caller may not administer");
        }
    }

    [Fact]
    public async Task ASiteAdminCannotRemoveAGrantOnAnotherSite()
    {
        var (root, scope, admin, targetId) = await ActingAsAsync(GlobalAdminPrincipal());
        using (root) using (scope)
        {
            // Seeded by a global Admin, then attacked by the site admin.
            var seeded = await admin.AddMembershipAsync(
                targetId, MembershipTargetType.Site, OtherSite, SiteRole.SiteViewer);
            seeded.Succeeded.Should().BeTrue(seeded.Error);

            var grants = await admin.GetSiteAccessAsync(OtherSite);
            var membershipId = grants.Single(g => g.UserId == targetId).MembershipId;

            scope.ServiceProvider.GetRequiredService<ICallerContext>()
                .SetUser(CallerInfo.ForUser(SiteAdminPrincipal(), null, null, "corr-2"));

            var result = await admin.RemoveMembershipAsync(targetId, membershipId);

            result.Succeeded.Should().BeFalse(
                "a membership id says nothing about its site, so the target has to be read before deleting");
        }
    }

    [Fact]
    public async Task AGlobalAdminIsNotRestricted()
    {
        var (root, scope, admin, targetId) = await ActingAsAsync(GlobalAdminPrincipal());
        using (root) using (scope)
        {
            var result = await admin.AddMembershipAsync(
                targetId, MembershipTargetType.AllSites, null, SiteRole.SiteAdmin);

            result.Succeeded.Should().BeTrue(result.Error);
        }
    }

    public void Dispose()
    {
        // SQLite may still hold the file when the provider's pooled connections have not been
        // finalized yet; the temp file is disposable either way.
        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }
}
