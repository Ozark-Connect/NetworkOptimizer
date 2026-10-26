using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// A console that rejects the password or hits its login attempt limit must not see another sign-in
/// on every call. Each failed sign-in counts toward the console's limit, which locks the account and
/// then refuses the corrected password too.
/// </summary>
public sealed class UniFiApiClientLoginGuardTests
{
    private static UniFiApiClient NewClient(FakeUniFiConsole console) =>
        new(NullLogger<UniFiApiClient>.Instance, console.Host, "User1", "pw");

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ARejectedPassword_IsNotRetriedWithinTheInterval(HttpStatusCode rejection)
    {
        await using var console = new FakeUniFiConsole(rejection);
        using var client = NewClient(console);

        (await client.LoginAsync()).Should().BeFalse();
        client.PasswordRejected.Should().BeTrue();

        (await client.LoginAsync()).Should().BeFalse();
        (await client.LoginAsync()).Should().BeFalse();

        console.LoginAttempts.Should().Be(1, "a rejected password waits RejectedPasswordRetryInterval before the next try");
    }

    [Fact]
    public async Task TheLoginLimit_HoldsFurtherSignIns()
    {
        await using var console = new FakeUniFiConsole(HttpStatusCode.TooManyRequests);
        using var client = NewClient(console);

        (await client.LoginAsync()).Should().BeFalse();
        client.LoginLimitBackoff.Should().Be(UniFiApiClient.LoginLimitInitialBackoff);
        client.LoginLimitedUntil.Should().BeAfter(DateTime.UtcNow);

        (await client.LoginAsync()).Should().BeFalse();
        console.LoginAttempts.Should().Be(1, "each sign-in during the limit is another attempt against it");
    }

    [Fact]
    public async Task ACarriedLoginLimit_HoldsANewClient()
    {
        // The reconnects build a new client each time; the limit has to survive that.
        await using var console = new FakeUniFiConsole(HttpStatusCode.TooManyRequests);
        using var client = NewClient(console);
        client.CarryLoginLimit(DateTime.UtcNow.AddSeconds(30), UniFiApiClient.LoginLimitInitialBackoff);

        (await client.LoginAsync()).Should().BeFalse();

        console.LoginAttempts.Should().Be(0);
    }

    [Fact]
    public async Task TheLoginLimitBackoff_DoublesUpToItsCap()
    {
        await using var console = new FakeUniFiConsole(HttpStatusCode.TooManyRequests);

        using (var client = NewClient(console))
        {
            client.CarryLoginLimit(DateTime.UtcNow.AddSeconds(-1), UniFiApiClient.LoginLimitInitialBackoff);
            await client.LoginAsync();
            client.LoginLimitBackoff.Should().Be(UniFiApiClient.LoginLimitInitialBackoff * 2);
        }

        using (var client = NewClient(console))
        {
            client.CarryLoginLimit(DateTime.UtcNow.AddSeconds(-1), UniFiApiClient.LoginLimitMaxBackoff);
            await client.LoginAsync();
            client.LoginLimitBackoff.Should().Be(UniFiApiClient.LoginLimitMaxBackoff);
        }

        console.LoginAttempts.Should().Be(2);
    }

    [Fact]
    public async Task AConsoleBehindADownProxy_IsNotARejectedPassword()
    {
        // A 502 never reached the console's auth, so it must not hold the password.
        await using var console = new FakeUniFiConsole(HttpStatusCode.BadGateway);
        using var client = NewClient(console);

        (await client.LoginAsync()).Should().BeFalse();

        client.PasswordRejected.Should().BeFalse();
        client.LastLoginUnreachable.Should().BeTrue();
    }
}
