using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Auditing;
using NetworkOptimizer.Web.Services.Identity;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

/// <summary>
/// Federation JIT/linking/role-mapping proven WITHOUT a live IdP (design doc 03): a fake external
/// principal (example.com / RFC 5737 data) is run through <see cref="ExternalLoginService"/> and the
/// resulting local user, external link, roles, and memberships are asserted. Covers CreateOnFirstLogin,
/// re-login via the existing link, JIT-off rejection, IdP-authoritative resync with the last-admin
/// guard, and the no-email-auto-linking rule.
/// </summary>
public sealed class ExternalLoginServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"netopt-fed-test-{Guid.NewGuid():N}.db");

    private async Task<ServiceProvider> BuildAsync()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>().UseSqlite($"Data Source={_dbPath}").Options;
        await using (var db = new NetworkOptimizerDbContext(options))
            await db.Database.MigrateAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton<IDbContextFactory<NetworkOptimizerDbContext>>(new NetworkOptimizerDbContextFactory(options));
        services.AddNetOptIdentityCore(_dbPath);
        services.AddSingleton<IAuditLogger>(new NoOpAudit());
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddCookie(IdentityConstants.ApplicationScheme);
        services.AddSingleton<IAuthenticationService, NoOpAuthService>();
        var staticAccessor = new StaticHttpContextAccessor();
        services.AddSingleton<IHttpContextAccessor>(staticAccessor);

        var provider = services.BuildServiceProvider();
        // SignInManager reads HttpContext; give it a fixed one (AsyncLocal wouldn't flow from here).
        staticAccessor.HttpContext = new DefaultHttpContext { RequestServices = provider };

        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IIdentityBootstrapService>().RunAsync();
        return provider;
    }

    private static ClaimsPrincipal External(string subject, string username, string? email = null, params string[] groups)
    {
        var claims = new List<Claim> { new("sub", subject), new("preferred_username", username) };
        if (email is not null) claims.Add(new Claim("email", email));
        claims.AddRange(groups.Select(g => new Claim("groups", g)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "oidc"));
    }

    private static FederationProvider Provider(
        JitProvisioningMode jit = JitProvisioningMode.CreateOnFirstLogin,
        RoleMappingMode mode = RoleMappingMode.Manual,
        params (string group, string role)[] roleMappings)
        => new()
        {
            Type = FederationProviderType.Oidc,
            Scheme = "okta",
            DisplayName = "Okta",
            JitProvisioning = jit,
            RoleMappingMode = mode,
            RoleMappings = roleMappings.Select(m => new FederationRoleMapping { GroupOrClaimValue = m.group, GlobalRole = m.role }).ToList(),
        };

    [Fact]
    public async Task Jit_CreatesUser_Links_AndMapsRole()
    {
        await using var provider = await BuildAsync();
        using var scope = provider.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IExternalLoginService>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var fed = Provider(roleMappings: ("netopt-ops", Roles.Operator));
        var outcome = await svc.ProcessAsync(fed, External("okta|001", "alice", "alice@example.com", "netopt-ops"));

        outcome.Should().Be(ExternalLoginOutcome.SignedIn);
        var user = await userManager.FindByLoginAsync("oidc:okta", "okta|001");
        user.Should().NotBeNull();
        user!.UserName.Should().Be("alice");
        (await userManager.IsInRoleAsync(user, Roles.Operator)).Should().BeTrue();
    }

    [Fact]
    public async Task ExistingLink_SignsInSameUser()
    {
        await using var provider = await BuildAsync();
        using var scope = provider.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IExternalLoginService>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var fed = Provider();

        await svc.ProcessAsync(fed, External("okta|002", "bob"));
        await svc.ProcessAsync(fed, External("okta|002", "bob-renamed-upstream"));

        userManager.Users.Count(u => u.UserName!.StartsWith("bob")).Should().Be(1, "the existing link is reused");
    }

    [Fact]
    public async Task JitOff_NoLink_IsRejected()
    {
        await using var provider = await BuildAsync();
        using var scope = provider.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IExternalLoginService>();
        var fed = Provider(jit: JitProvisioningMode.Off);

        (await svc.ProcessAsync(fed, External("okta|003", "carol")))
            .Should().Be(ExternalLoginOutcome.NoAccount);
    }

    [Fact]
    public async Task NoEmailAutoLink_SameEmailMakesSeparateUsers()
    {
        await using var provider = await BuildAsync();
        using var scope = provider.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IExternalLoginService>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var fed = Provider();

        var o1 = await svc.ProcessAsync(fed, External("okta|100", "dave", "shared@example.com"));
        var o2 = await svc.ProcessAsync(fed, External("okta|200", "dave", "shared@example.com"));

        o1.Should().Be(ExternalLoginOutcome.SignedIn);
        o2.Should().Be(ExternalLoginOutcome.SignedIn);
        // Same email, different subjects -> two distinct users (collision-suffixed username), never merged.
        var u1 = await userManager.FindByLoginAsync("oidc:okta", "okta|100");
        var u2 = await userManager.FindByLoginAsync("oidc:okta", "okta|200");
        u1.Should().NotBeNull();
        u2.Should().NotBeNull();
        u1!.Id.Should().NotBe(u2!.Id);
    }

    [Fact]
    public async Task IdpAuthoritative_Resync_SkipsLastAdminDemotion()
    {
        await using var provider = await BuildAsync();
        using var scope = provider.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IExternalLoginService>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        // Provision a federated user as Admin, then re-login with no admin group under authoritative mode.
        var fedGrant = Provider(mode: RoleMappingMode.IdpAuthoritative, roleMappings: ("admins", Roles.Admin));
        await svc.ProcessAsync(fedGrant, External("okta|900", "erin", groups: "admins"));
        var erin = await userManager.FindByLoginAsync("oidc:okta", "okta|900");

        // Remove the seeded local admin so erin is the ONLY enabled Admin.
        var seededAdmin = await userManager.FindByNameAsync(IdentityBootstrapService.AdminUserName);
        if (seededAdmin is not null) { seededAdmin.IsEnabled = false; await userManager.UpdateAsync(seededAdmin); }

        var fedRevoke = Provider(mode: RoleMappingMode.IdpAuthoritative, roleMappings: ("admins", Roles.Admin));
        await svc.ProcessAsync(fedRevoke, External("okta|900", "erin")); // no groups now

        (await userManager.IsInRoleAsync(erin!, Roles.Admin))
            .Should().BeTrue("resync must not demote the last remaining Admin");
    }

    private sealed class StaticHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class NoOpAudit : IAuditLogger { public void Log(AuditEvent e) { } }

    private sealed class NoOpAuthService : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext c, string? s) => Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext c, string? s, AuthenticationProperties? p) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext c, string? s, AuthenticationProperties? p) => Task.CompletedTask;
        public Task SignInAsync(HttpContext c, string? s, ClaimsPrincipal p, AuthenticationProperties? pr) => Task.CompletedTask;
        public Task SignOutAsync(HttpContext c, string? s, AuthenticationProperties? p) => Task.CompletedTask;
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch (IOException) { }
    }
}
