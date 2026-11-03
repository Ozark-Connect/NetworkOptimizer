using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>Logout takes the path of the sign-in it ends: UniFi OS answers /api/logout with 404.</summary>
public sealed class UniFiApiClientLogoutTests
{
    private static UniFiApiClient NewClient(FakeUniFiConsole console) =>
        new(NullLogger<UniFiApiClient>.Instance, console.Host, "User1", "pw");

    [Fact]
    public async Task UniFiOs_LogsOutAtApiAuthLogout()
    {
        await using var console = new FakeUniFiConsole(HttpStatusCode.OK);
        using var client = NewClient(console);
        (await client.LoginAsync()).Should().BeTrue();

        (await client.LogoutAsync()).Should().BeTrue();

        console.Requests.Should().Contain("POST /api/auth/logout").And.NotContain("POST /api/logout");
    }

    [Fact]
    public async Task StandaloneNetworkServer_LogsOutAtApiLogout()
    {
        await using var console = new FakeUniFiConsole(HttpStatusCode.OK) { Standalone = true };
        using var client = NewClient(console);
        (await client.LoginAsync()).Should().BeTrue();
        client.IsStandaloneNetworkController.Should().BeTrue();

        (await client.LogoutAsync()).Should().BeTrue();

        console.Requests.Should().Contain("POST /api/logout").And.NotContain("POST /api/auth/logout");
    }
}
