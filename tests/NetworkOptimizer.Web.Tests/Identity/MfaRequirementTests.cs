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
