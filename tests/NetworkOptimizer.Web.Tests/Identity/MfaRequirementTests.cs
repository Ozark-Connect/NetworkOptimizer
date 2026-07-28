using FluentAssertions;
using Microsoft.AspNetCore.Authentication.Cookies;
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
/// A role's MFA requirement is satisfied by any second factor, not specifically by TOTP. A passkey is
/// origin-bound and therefore phishing-resistant where a typed code is not, so pushing a passkey user
/// into enrolling an authenticator app would be demanding the weaker factor.
/// </summary>
public sealed class MfaRequirementTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"netopt-mfa-test-{Guid.NewGuid():N}.db");

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

        // SignInManager issues and clears the auth cookie, so the sign-in path needs real
        // authentication schemes and an HttpContext to write to.
        services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.TwoFactorUserIdScheme);
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.AddSingleton<IAuthPolicyOptions, StubAuthPolicy>();
        services.AddGatePlumbing();
        return services.BuildServiceProvider();
    }

    private async Task<ServiceProvider> BuildWithSchemaAsync()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        await using (var db = new NetworkOptimizerDbContext(options))
            await db.Database.MigrateAsync();

        var provider = BuildProvider();
        using (var scope = provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IIdentityBootstrapService>().RunAsync();
        return provider;
    }

    /// <summary>Gives the scope an HttpContext so SignInManager can write and clear its cookie.</summary>
    private static void AttachHttpContext(IServiceScope scope)
    {
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
    }

    /// <summary>Local login stays enabled; the SSO-only toggle is not what these tests exercise.</summary>
    private sealed class StubAuthPolicy : IAuthPolicyOptions
    {
        public Task<bool> IsLocalLoginDisabledAsync() => Task.FromResult(false);
        public Task SetLocalLoginDisabledAsync(bool disabled) => Task.CompletedTask;
        public Task<bool> IsRestrictSitesToMembersAsync() => Task.FromResult(false);
        public Task SetRestrictSitesToMembersAsync(bool restrict) => Task.CompletedTask;
    }

    private static async Task<ApplicationUser> CreateUserAsync(IServiceScope scope, string name)
    {
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = name, IsEnabled = true };
        (await userManager.CreateAsync(user, "Some-Pass-9")).Succeeded.Should().BeTrue();
        return user;
    }

    /// <summary>
    /// Registers a passkey through the supported UserManager path; the WebAuthn ceremony that would
    /// normally produce the credential is a browser concern and not what this test is about.
    /// </summary>
    private static async Task AddPasskeyAsync(IServiceScope scope, ApplicationUser user)
    {
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var passkey = new UserPasskeyInfo(
            credentialId: Guid.NewGuid().ToByteArray(),
            publicKey: new byte[] { 1, 2, 3, 4 },
            createdAt: DateTimeOffset.UtcNow,
            signCount: 0,
            transports: null,
            isUserVerified: true,
            isBackupEligible: false,
            isBackedUp: false,
            attestationObject: Array.Empty<byte>(),
            clientDataJson: Array.Empty<byte>())
        {
            Name = "My phone",
        };
        (await userManager.AddOrUpdatePasskeyAsync(user, passkey)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task APasskeyAloneSatisfiesTheRequirement()
    {
        await using var provider = await BuildWithSchemaAsync();
        using var scope = provider.CreateScope();
        var user = await CreateUserAsync(scope, "passkeyonly");
        await AddPasskeyAsync(scope, user);

        var mfa = scope.ServiceProvider.GetRequiredService<IMfaService>();

        (await mfa.HasSecondFactorAsync(user)).Should()
            .BeTrue("a passkey is a second factor, and a phishing-resistant one");
        (await mfa.IsEnabledAsync(user)).Should()
            .BeFalse("the authenticator app card must still report TOTP honestly");
    }

    [Fact]
    public async Task NoFactorAtAllDoesNotSatisfyTheRequirement()
    {
        await using var provider = await BuildWithSchemaAsync();
        using var scope = provider.CreateScope();
        var user = await CreateUserAsync(scope, "nofactor");

        var mfa = scope.ServiceProvider.GetRequiredService<IMfaService>();
        (await mfa.HasSecondFactorAsync(user)).Should().BeFalse();
    }

    [Fact]
    public async Task AnAuthenticatorAppStillSatisfiesTheRequirement()
    {
        await using var provider = await BuildWithSchemaAsync();
        using var scope = provider.CreateScope();
        var user = await CreateUserAsync(scope, "totponly");

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        (await userManager.SetTwoFactorEnabledAsync(user, true)).Succeeded.Should().BeTrue();

        var mfa = scope.ServiceProvider.GetRequiredService<IMfaService>();
        (await mfa.HasSecondFactorAsync(user)).Should().BeTrue();
        (await mfa.IsEnabledAsync(user)).Should().BeTrue();
    }

    [Fact]
    public async Task PasswordSignInIsRefusedWhenTheOnlySecondFactorIsAPasskey()
    {
        await using var provider = await BuildWithSchemaAsync();

        using (var scope = provider.AdminScope())
        {
            var user = await CreateUserAsync(scope, "passkeyadmin");
            await AddPasskeyAsync(scope, user);

            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            (await userManager.AddToRoleAsync(user, Roles.Admin)).Succeeded.Should().BeTrue();

            var identityAdmin = scope.ServiceProvider.GetRequiredService<IIdentityAdminService>();
            (await identityAdmin.SetRoleRequiresMfaAsync(Roles.Admin, true)).Succeeded.Should().BeTrue();
        }

        using (var scope = provider.CreateScope())
        {
            AttachHttpContext(scope);
            var signIn = scope.ServiceProvider.GetRequiredService<IIdentitySignInService>();
            var outcome = await signIn.PasswordSignInAsync("passkeyadmin", "Some-Pass-9", rememberMe: false);

            outcome.Should().Be(SignInOutcome.RequiresPasskeySignIn,
                "a password alone is single-factor, and Identity never challenges the passkey after one");
        }
    }

    [Fact]
    public async Task PasswordSignInStillSucceedsWhenTheRoleDoesNotRequireASecondFactor()
    {
        await using var provider = await BuildWithSchemaAsync();

        using (var scope = provider.CreateScope())
        {
            var user = await CreateUserAsync(scope, "plainviewer");
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            (await userManager.AddToRoleAsync(user, Roles.Viewer)).Succeeded.Should().BeTrue();
        }

        using (var scope = provider.CreateScope())
        {
            AttachHttpContext(scope);
            var signIn = scope.ServiceProvider.GetRequiredService<IIdentitySignInService>();
            (await signIn.PasswordSignInAsync("plainviewer", "Some-Pass-9", rememberMe: false))
                .Should().Be(SignInOutcome.Success, "nothing changes for a role with no MFA requirement");
        }
    }

    [Fact]
    public async Task ADisabledAccountCannotSignInWithItsPasskey()
    {
        await using var provider = await BuildWithSchemaAsync();

        using (var scope = provider.CreateScope())
        {
            var created = await CreateUserAsync(scope, "disabledpasskey");
            await AddPasskeyAsync(scope, created);

            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            created.IsEnabled = false;
            (await userManager.UpdateAsync(created)).Succeeded.Should().BeTrue();
        }

        using (var scope = provider.CreateScope())
        {
            AttachHttpContext(scope);
            // Resolved inside the acting scope, as the endpoint does with the assertion's user.
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByNameAsync("disabledpasskey");
            var signIn = scope.ServiceProvider.GetRequiredService<IIdentitySignInService>();

            (await signIn.PasskeySignInAsync(user!, rememberMe: false)).Should().Be(SignInOutcome.Failed,
                "a credential registered before the account was disabled must not still let it in");
        }
    }

    [Fact]
    public async Task APasskeySignInRecordsHowTheUserGotIn()
    {
        await using var provider = await BuildWithSchemaAsync();

        using (var scope = provider.CreateScope())
        {
            var created = await CreateUserAsync(scope, "passkeylogin");
            await AddPasskeyAsync(scope, created);
        }

        using (var scope = provider.CreateScope())
        {
            AttachHttpContext(scope);
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByNameAsync("passkeylogin");
            var signIn = scope.ServiceProvider.GetRequiredService<IIdentitySignInService>();
            (await signIn.PasskeySignInAsync(user!, rememberMe: false)).Should().Be(SignInOutcome.Success);
        }

        using (var scope = provider.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var stored = await userManager.FindByNameAsync("passkeylogin");
            stored!.LastLoginMethod.Should().Be("passkey",
                "the Identity tab reports how each account last signed in");
            stored.LastLoginAt.Should().NotBeNull();
        }
    }

    /// <summary>
    /// These tests never exercise the membership ownership guard, so the resolver only has to exist
    /// for the container to build.
    /// </summary>
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
