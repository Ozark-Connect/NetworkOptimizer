using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
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
/// The background reconnect belongs to the connection service, so a console that goes down comes back
/// whatever else is enabled. These pin when a background try signs in, and when it must not.
/// </summary>
public sealed class UniFiConnectionServiceAutoReconnectTests
{
    private const string SiteSlug = "test-site";

    private static UniFiConnectionService NewService(FakeUniFiConsole console, bool rememberCredentials = true)
    {
        var settings = new UniFiConnectionSettings
        {
            ControllerUrl = console.Host,
            Username = "User1",
            Password = "encrypted",
            IsConfigured = true,
            RememberCredentials = rememberCredentials,
        };
        var repository = new Mock<IUniFiRepository>();
        repository.Setup(r => r.GetUniFiConnectionSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
        var credentials = new Mock<ICredentialProtectionService>();
        credentials.Setup(c => c.Decrypt(It.IsAny<string>())).Returns("pw");

        var services = new ServiceCollection();
        services.AddSingleton(new SiteContextService(new HttpContextAccessor(),
            new SiteDatabasePaths(Path.Combine(Path.GetTempPath(), "unifi-reconnect-tests.db"))));
        services.AddScoped(_ => repository.Object);

        return new UniFiConnectionService(NullLogger<UniFiConnectionService>.Instance, NullLoggerFactory.Instance,
            services.BuildServiceProvider(), credentials.Object, SiteSlug);
    }

    private static async Task WaitForInitializedAsync(UniFiConnectionService service)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!service.IsInitialized && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        service.IsInitialized.Should().BeTrue();
    }

    [Fact]
    public async Task AConsoleThatFailedToConnect_IsRetriedInTheBackground()
    {
        await using var console = new FakeUniFiConsole(HttpStatusCode.BadGateway);
        var service = NewService(console);
        try
        {
            await WaitForInitializedAsync(service);
            var before = console.LoginAttempts;
            before.Should().BeGreaterThan(0, "the startup connect signs in");

            await service.AutoReconnectTickAsync();

            console.LoginAttempts.Should().BeGreaterThan(before, "no feature loop is needed to retry the console");
        }
        finally { service.DisposeOwned(); }
    }

    [Fact]
    public async Task ADisconnectInSettings_IsNotUndoneByTheBackgroundReconnect()
    {
        await using var console = new FakeUniFiConsole(HttpStatusCode.BadGateway);
        var service = NewService(console);
        try
        {
            await WaitForInitializedAsync(service);
            await service.DisconnectAsync();
            var before = console.LoginAttempts;

            await service.AutoReconnectTickAsync();

            console.LoginAttempts.Should().Be(before);
        }
        finally { service.DisposeOwned(); }
    }

    [Fact]
    public async Task RememberCredentialsOff_NeverOpensAConnectionStartupDeclined()
    {
        await using var console = new FakeUniFiConsole(HttpStatusCode.BadGateway);
        var service = NewService(console, rememberCredentials: false);
        try
        {
            await WaitForInitializedAsync(service);

            await service.AutoReconnectTickAsync();

            console.LoginAttempts.Should().Be(0);
        }
        finally { service.DisposeOwned(); }
    }

    [Fact]
    public async Task ARejectedSavedPassword_HoldsTheBackgroundReconnectToo()
    {
        await using var console = new FakeUniFiConsole(HttpStatusCode.Unauthorized);
        var service = NewService(console);
        try
        {
            await WaitForInitializedAsync(service);
            var before = console.LoginAttempts;

            for (var i = 0; i < 3; i++)
                await service.AutoReconnectTickAsync();

            console.LoginAttempts.Should().Be(before);
        }
        finally { service.DisposeOwned(); }
    }
}
