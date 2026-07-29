using System.Security.Claims;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Identity;
using NetworkOptimizer.Web.Services.Authorization;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

/// <summary>
/// Revoking a site has to take effect on the next read, not at the end of a cache window. The
/// authorized-slug set is what decides which sites a user is even shown, so a stale one leaves a
/// revoked site sitting in their switcher and on /sites - visible, and looking like the revoke failed.
/// </summary>
public sealed class SiteAccessInvalidationTests : IDisposable
{
    private readonly string _authDbPath = Path.Combine(Path.GetTempPath(), $"netopt-access-auth-{Guid.NewGuid():N}.db");
    private readonly string _mainDbPath = Path.Combine(Path.GetTempPath(), $"netopt-access-main-{Guid.NewGuid():N}.db");
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    // One instance across every resolver the test builds, matching the singleton registration: the
    // invalidation tokens are what let one resolver drop entries another one wrote.
    private readonly SiteRoleCacheTokens _tokens = new();

    private const string UserId = "user-1";

    [Fact]
    public async Task RevokingASiteDropsItFromTheAuthorizedSetImmediately()
    {
        await SeedAsync();
        var resolver = BuildResolver();
        var principal = PrincipalFor(UserId, Roles.Viewer);

        (await resolver.GetAuthorizedSlugsAsync(principal)).Should()
            .Contain("branch", "the membership is in place to begin with");

        await RemoveMembershipAsync("branch");
        resolver.Invalidate(UserId);

        (await resolver.GetAuthorizedSlugsAsync(principal)).Should()
            .NotContain("branch", "the revoke must apply to the very next read, not in ten minutes");
        (await resolver.GetEffectiveRoleAsync(principal, "branch")).Should()
            .BeNull("and the site must stop being operable at the same moment");
    }

    [Fact]
    public async Task ARevokeSurvivesAConcurrentRebuildOfTheSameUser()
    {
        // The real sequence: a membership change invalidates and then broadcasts, and every open
        // circuit and in-flight request rebuilds this user's set at the same instant.
        //
        // HONEST LIMIT: this is a smoke test, not a reproduction. Run against the non-atomic token
        // acquisition this replaced, it still passes - the racers stagger behind their database
        // reads and rarely collide inside the token factory. Reproducing that reliably needs a cache
        // decorator that holds two threads at a barrier, and the fixed code never consults the cache
        // for a token at all, so the same harness cannot drive both. What guards the invariant now
        // is the type: ConcurrentDictionary.GetOrAdd hands every caller the one stored source.
        await SeedAsync();
        var principal = PrincipalFor(UserId, Roles.Viewer);

        for (var attempt = 0; attempt < 50; attempt++)
        {
            await AddMembershipAsync("depot", SiteRole.SiteViewer);
            BuildResolver().Invalidate(UserId);

            // Separate instances, as production has: a scope per circuit, one shared cache.
            await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => Task.Run(() => BuildResolver().GetAuthorizedSlugsAsync(principal))));

            await RemoveMembershipAsync("depot");
            BuildResolver().Invalidate(UserId);

            (await BuildResolver().GetAuthorizedSlugsAsync(principal)).Should()
                .NotContain("depot", $"the revoke must evict every cached set (attempt {attempt})");
        }
    }

    [Fact]
    public async Task AddingASiteAppearsImmediatelyToo()
    {
        await SeedAsync();
        var resolver = BuildResolver();
        var principal = PrincipalFor(UserId, Roles.Viewer);

        (await resolver.GetAuthorizedSlugsAsync(principal)).Should().NotContain("depot");

        await AddMembershipAsync("depot", SiteRole.SiteViewer);
        resolver.Invalidate(UserId);

        (await resolver.GetAuthorizedSlugsAsync(principal)).Should().Contain("depot");
    }

    private readonly RestrictedPolicy _policy = new();

    private EffectiveSiteRoleResolver BuildResolver() => new(
        new AuthDbContextFactory(_authDbPath),
        new MainDbContextFactory(_mainDbPath),
        _policy,
        _cache,
        _tokens);

    /// <summary>
    /// Turning the site restriction off has to apply now, not in ten minutes. It is one global setting
    /// that decides whether a global Operator or Viewer role reaches every site, so the answer it
    /// changes is already cached for every user and every site - and nothing about the user changed,
    /// so none of the per-user invalidation fires. Toggling it appeared to do nothing at all.
    /// </summary>
    [Fact]
    public async Task LiftingTheSiteRestrictionAppliesOnTheNextRead()
    {
        await SeedAsync();
        var resolver = BuildResolver();
        var principal = PrincipalFor(UserId, Roles.Operator);

        (await resolver.GetEffectiveRoleAsync(principal, "depot")).Should()
            .BeNull("restricted, and the account holds no grant on depot");

        _policy.Restrict = false;
        (await resolver.GetEffectiveRoleAsync(principal, "depot")).Should()
            .BeNull("the cached answer stands until something drops it - this is the stale read itself");

        _tokens.InvalidateAll();
        (await resolver.GetEffectiveRoleAsync(principal, "depot")).Should()
            .Be(SiteRole.SiteOperator, "unrestricted, a global Operator role reaches every site");
    }

    private async Task SeedAsync()
    {
        await using (var main = new MainDbContextFactory(_mainDbPath).CreateDbContext())
        {
            await main.Database.MigrateAsync();
            main.Sites.AddRange(
                new Site { Slug = "main", Name = "Main", IsDefault = true, Enabled = true },
                new Site { Slug = "branch", Name = "Branch", Enabled = true },
                new Site { Slug = "depot", Name = "Depot", Enabled = true });
            await main.SaveChangesAsync();
        }

        await using var auth = new AuthDbContextFactory(_authDbPath).CreateDbContext();
        await auth.Database.MigrateAsync();
        // The membership hangs off a real account row.
        auth.Users.Add(new ApplicationUser
        {
            Id = UserId,
            UserName = "member",
            NormalizedUserName = "MEMBER",
            SecurityStamp = Guid.NewGuid().ToString(),
        });
        await auth.SaveChangesAsync();
        auth.SiteMemberships.Add(new SiteMembership
        {
            UserId = UserId,
            TargetType = MembershipTargetType.Site,
            TargetId = "branch",
            SiteRole = SiteRole.SiteViewer,
        });
        await auth.SaveChangesAsync();
    }

    private async Task RemoveMembershipAsync(string slug)
    {
        await using var auth = new AuthDbContextFactory(_authDbPath).CreateDbContext();
        await auth.SiteMemberships.Where(m => m.UserId == UserId && m.TargetId == slug).ExecuteDeleteAsync();
    }

    private async Task AddMembershipAsync(string slug, SiteRole role)
    {
        await using var auth = new AuthDbContextFactory(_authDbPath).CreateDbContext();
        auth.SiteMemberships.Add(new SiteMembership
        {
            UserId = UserId,
            TargetType = MembershipTargetType.Site,
            TargetId = slug,
            SiteRole = role,
        });
        await auth.SaveChangesAsync();
    }

    private static ClaimsPrincipal PrincipalFor(string userId, string role) => new(
        new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Role, role),
            },
            "test"));

    /// <summary>The install has the site restriction on, which is the shipped default.</summary>
    private sealed class RestrictedPolicy : IAuthPolicyOptions
    {
        /// <summary>Flipped directly, standing in for the toggle in Settings - Identity.</summary>
        public bool Restrict { get; set; } = true;

        public Task<bool> IsLocalLoginDisabledAsync() => Task.FromResult(false);
        public Task SetLocalLoginDisabledAsync(bool disabled) => Task.CompletedTask;
        public Task<bool> IsRestrictSitesToMembersAsync() => Task.FromResult(Restrict);
        public Task SetRestrictSitesToMembersAsync(bool restrict)
        {
            Restrict = restrict;
            return Task.CompletedTask;
        }
    }

    private sealed class AuthDbContextFactory : IDbContextFactory<AuthDbContext>
    {
        private readonly string _path;
        public AuthDbContextFactory(string path) => _path = path;

        public AuthDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<AuthDbContext>().UseSqlite($"Data Source={_path}").Options);
    }

    private sealed class MainDbContextFactory : IDbContextFactory<NetworkOptimizerDbContext>
    {
        private readonly string _path;
        public MainDbContextFactory(string path) => _path = path;

        public NetworkOptimizerDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<NetworkOptimizerDbContext>().UseSqlite($"Data Source={_path}").Options);
    }

    public void Dispose()
    {
        _cache.Dispose();
        foreach (var path in new[] { _authDbPath, _mainDbPath })
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { /* temp file, ignored */ }
        }
    }
}
