using FluentAssertions;
using Microsoft.AspNetCore.Http;
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
/// SSO-only refuses local sign-in only while an identity provider can sign someone in. An enabled
/// provider held inert by a bad setting is not one, so it must not lock everyone out.
/// </summary>
public sealed class SsoOnlySignInTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"netopt-ssoonly-test-{Guid.NewGuid():N}.db");

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

        services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.TwoFactorUserIdScheme);
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.AddSingleton<IAuthPolicyOptions, SsoOnlyPolicy>();
        services.AddGatePlumbing();
        return services.BuildServiceProvider();
    }

    private async Task<ServiceProvider> BuildWithUserAsync()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        await using (var db = new NetworkOptimizerDbContext(options))
            await db.Database.MigrateFromTemplateAsync();

        var provider = BuildProvider();
        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IIdentityBootstrapService>().RunAsync();

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = "localuser", IsEnabled = true };
        (await userManager.CreateAsync(user, "Some-Pass-9")).Succeeded.Should().BeTrue();
        (await userManager.AddToRoleAsync(user, Roles.Viewer)).Succeeded.Should().BeTrue();
        return provider;
    }

    /// <summary>Writes the row straight to the database, as a restored backup or an older build would.</summary>
    private static async Task AddEnabledOidcAsync(ServiceProvider provider, string authority)
    {
        var factory = provider.GetRequiredService<IDbContextFactory<AuthDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        db.FederationProviders.Add(new FederationProvider
        {
            Scheme = "corp",
            DisplayName = "Corp",
            Type = FederationProviderType.Oidc,
            Enabled = true,
            Authority = authority,
            ClientId = "client",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<SignInOutcome> SignInAsync(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext =
            new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        return await scope.ServiceProvider.GetRequiredService<IIdentitySignInService>()
            .PasswordSignInAsync("localuser", "Some-Pass-9", rememberMe: false);
    }

    [Fact]
    public async Task AUsableProviderKeepsLocalSignInRefused()
    {
        await using var provider = await BuildWithUserAsync();
        await AddEnabledOidcAsync(provider, "https://idp.example.com");

        (await SignInAsync(provider)).Should().Be(SignInOutcome.LocalLoginDisabled);
    }

    [Fact]
    public async Task AnInertProviderDoesNotLockEveryoneOut()
    {
        await using var provider = await BuildWithUserAsync();
        await AddEnabledOidcAsync(provider, "http://idp.example.com");

        (await SignInAsync(provider)).Should().Be(SignInOutcome.Success,
            "the login page shows the local form when no provider can sign anyone in");
    }

    private sealed class SsoOnlyPolicy : IAuthPolicyOptions
    {
        public Task<bool> IsLocalLoginDisabledAsync() => Task.FromResult(true);
        public Task SetLocalLoginDisabledAsync(bool disabled) => Task.CompletedTask;
        public Task<bool> IsRestrictSitesToMembersAsync() => Task.FromResult(false);
        public Task SetRestrictSitesToMembersAsync(bool restrict) => Task.CompletedTask;
    }

    /// <summary>Never consulted here; it only has to exist for the container to build.</summary>
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
