using System.Diagnostics;
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

/// <summary>
/// A main site marked "No UniFi Console at this site" never signs in to its saved console, and stops
/// making pages wait for one. Clearing the flag signs in again with the saved settings.
/// </summary>
public sealed class UniFiConnectionServiceNoConsoleTests
{
    private sealed class TestDbFactory : IDbContextFactory<NetworkOptimizerDbContext>
    {
        private readonly DbContextOptions<NetworkOptimizerDbContext> _options;
        public TestDbFactory(DbContextOptions<NetworkOptimizerDbContext> options) => _options = options;
        public NetworkOptimizerDbContext CreateDbContext() => new(_options);
    }

    private readonly TestDbFactory _mainDb = new(new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    private async Task SeedAsync(bool hasNoConsole, bool multiSite = true)
    {
        await using var db = _mainDb.CreateDbContext();
        db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.MultiSiteEnabled, Value = multiSite.ToString() });
        db.Sites.Add(new Site
        {
            Slug = SiteManagementService.DefaultSiteSlug,
            Name = "Main Site",
            IsDefault = true,
            HasNoConsole = hasNoConsole,
        });
        await db.SaveChangesAsync();
    }

    private async Task SetFlagAsync(bool hasNoConsole)
    {
        await using var db = _mainDb.CreateDbContext();
        var site = await db.Sites.SingleAsync(s => s.IsDefault);
        site.HasNoConsole = hasNoConsole;
        await db.SaveChangesAsync();
    }

    private UniFiConnectionService NewService(FakeUniFiConsole console, string slug = SiteManagementService.DefaultSiteSlug)
    {
        var settings = new UniFiConnectionSettings
        {
            ControllerUrl = console.Host,
            Username = "User1",
            Password = "encrypted",
            IsConfigured = true,
            RememberCredentials = true,
        };
        var repository = new Mock<IUniFiRepository>();
        repository.Setup(r => r.GetUniFiConnectionSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
        var credentials = new Mock<ICredentialProtectionService>();
        credentials.Setup(c => c.Decrypt(It.IsAny<string>())).Returns("pw");

        var services = new ServiceCollection();
        services.AddSingleton(new SiteContextService(new HttpContextAccessor(),
            new SiteDatabasePaths(Path.Combine(Path.GetTempPath(), "unifi-no-console-tests.db"))));
        services.AddScoped(_ => repository.Object);
        services.AddSingleton<IDbContextFactory<NetworkOptimizerDbContext>>(_mainDb);

        return new UniFiConnectionService(NullLogger<UniFiConnectionService>.Instance, NullLoggerFactory.Instance,
            services.BuildServiceProvider(), credentials.Object, slug);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }

    [Fact]
    public async Task FlagSet_InitializesWithoutSigningIn()
    {
        await SeedAsync(hasNoConsole: true);
        await using var console = new FakeUniFiConsole(HttpStatusCode.Unauthorized);
        using var service = NewService(console);

        await service.EnsureInitializedAsync();

        service.IsInitialized.Should().BeTrue();
        service.HasNoConsole.Should().BeTrue();
        service.IsConnected.Should().BeFalse();
        (await service.ReconnectAsync()).Should().BeFalse();
        console.LoginAttempts.Should().Be(0);
    }

    [Fact]
    public async Task FlagSet_WaitForConnectionReturnsAtOnce()
    {
        await SeedAsync(hasNoConsole: true);
        await using var console = new FakeUniFiConsole(HttpStatusCode.Unauthorized);
        using var service = NewService(console);
        await service.EnsureInitializedAsync();

        var timer = Stopwatch.StartNew();
        (await service.WaitForConnectionAsync(TimeSpan.FromSeconds(10))).Should().BeFalse();

        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task FlagSet_SaveIsRefusedWithoutSigningIn()
    {
        await SeedAsync(hasNoConsole: true);
        await using var console = new FakeUniFiConsole(HttpStatusCode.Unauthorized);
        using var service = NewService(console);
        await service.EnsureInitializedAsync();

        var connected = await service.ConnectAsync(new UniFiConnectionConfig
        {
            ControllerUrl = console.Host,
            Username = "User1",
            Password = "pw",
        });

        connected.Should().BeFalse();
        service.LastError.Should().Contain("No UniFi Console at this site");
        console.LoginAttempts.Should().Be(0);
    }

    [Fact]
    public async Task FlagSetWithMultiSiteOff_IsIgnored()
    {
        await SeedAsync(hasNoConsole: true, multiSite: false);
        await using var console = new FakeUniFiConsole(HttpStatusCode.Unauthorized);
        using var service = NewService(console);

        await service.EnsureInitializedAsync();

        service.HasNoConsole.Should().BeFalse();
        console.LoginAttempts.Should().Be(1, "the startup connect signs in once");
    }

    [Fact]
    public async Task FlagOnAnotherSite_IsIgnored()
    {
        await SeedAsync(hasNoConsole: true);
        await using var console = new FakeUniFiConsole(HttpStatusCode.Unauthorized);
        using var service = NewService(console, slug: "branch-office");

        await service.EnsureInitializedAsync();

        service.HasNoConsole.Should().BeFalse();
        console.LoginAttempts.Should().Be(1);
    }

    [Fact]
    public async Task FlagCleared_SignsInWithTheSavedSettings()
    {
        await SeedAsync(hasNoConsole: true);
        await using var console = new FakeUniFiConsole(HttpStatusCode.Unauthorized);
        using var service = NewService(console);
        await service.EnsureInitializedAsync();
        var changes = 0;
        service.OnConnectionChanged += () => Interlocked.Increment(ref changes);

        await SetFlagAsync(false);
        await service.RefreshNoConsoleAsync();

        service.HasNoConsole.Should().BeFalse();
        console.LoginAttempts.Should().Be(1);
        changes.Should().BeGreaterThan(0, "pages re-render when the flag clears");
    }

    [Fact]
    public async Task FlagSetLater_StopsTheReconnects()
    {
        await SeedAsync(hasNoConsole: false);
        await using var console = new FakeUniFiConsole(HttpStatusCode.Unauthorized);
        using var service = NewService(console);
        await service.EnsureInitializedAsync();
        await WaitUntilAsync(() => console.LoginAttempts > 0);
        var attempts = console.LoginAttempts;

        await SetFlagAsync(true);
        await service.RefreshNoConsoleAsync();

        service.HasNoConsole.Should().BeTrue();
        service.LastError.Should().BeNull();
        (await service.ReconnectAsync()).Should().BeFalse();
        console.LoginAttempts.Should().Be(attempts);
    }
}
