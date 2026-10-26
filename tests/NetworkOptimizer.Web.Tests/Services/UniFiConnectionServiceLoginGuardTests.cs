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
/// The background reconnect builds a new client on every try (every 10 s while a console is down),
/// so the client's own holds do not carry over by themselves. These pin the service-level guards: a
/// rejected saved password and the console's login attempt limit must not turn each reconnect into
/// another failed sign-in.
/// </summary>
public sealed class UniFiConnectionServiceLoginGuardTests
{
    private const string SiteSlug = "test-site";

    private static UniFiConnectionService NewService(FakeUniFiConsole console)
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
            new SiteDatabasePaths(Path.Combine(Path.GetTempPath(), "unifi-guard-tests.db"))));
        services.AddScoped(_ => repository.Object);

        return new UniFiConnectionService(NullLogger<UniFiConnectionService>.Instance, NullLoggerFactory.Instance,
            services.BuildServiceProvider(), credentials.Object, SiteSlug);
    }

    /// <summary>The service signs in on its own at construction; wait for that first attempt.</summary>
    private static async Task WaitForFirstSignInAsync(FakeUniFiConsole console)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (console.LoginAttempts == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        console.LoginAttempts.Should().Be(1, "the startup connect signs in once");
    }

    [Fact]
    public async Task ARejectedSavedPassword_IsNotRetriedByTheReconnects()
    {
        await using var console = new FakeUniFiConsole(HttpStatusCode.Unauthorized);
        using var service = NewService(console);
        await WaitForFirstSignInAsync(console);

        for (var i = 0; i < 3; i++)
            (await service.ReconnectAsync()).Should().BeFalse();

        console.LoginAttempts.Should().Be(1, "the same saved password waits RejectedPasswordRetryInterval");
    }

    [Fact]
    public async Task TheLoginLimit_CarriesAcrossTheReconnects()
    {
        await using var console = new FakeUniFiConsole(HttpStatusCode.TooManyRequests);
        using var service = NewService(console);
        await WaitForFirstSignInAsync(console);

        for (var i = 0; i < 3; i++)
            (await service.ReconnectAsync()).Should().BeFalse();

        console.LoginAttempts.Should().Be(1, "each reconnect's new client inherits the login attempt limit");
    }
}
