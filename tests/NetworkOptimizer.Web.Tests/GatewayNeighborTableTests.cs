using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Ssh;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// The shared IPv6-to-device lookup behind Client Performance and client speed tests (#568).
/// </summary>
public class GatewayNeighborTableTests
{
    private const string NeighborOutput =
        "fd00::1234 dev br0 lladdr AA:BB:CC:DD:EE:01 STALE\n" +
        "2001:db8::20 dev br10 lladdr aa:bb:cc:dd:ee:02 REACHABLE\n";

    [Theory]
    [InlineData("fd00::1234", true)]
    [InlineData("2001:db8::20", true)]
    [InlineData("fe80::1", false)]              // Link-local needs a scope; never looked up
    [InlineData("::ffff:192.0.2.10", false)]    // IPv4-mapped is an IPv4 client
    [InlineData("192.0.2.10", false)]
    [InlineData("not-an-ip", false)]
    [InlineData(null, false)]
    public void IsResolvable_OnlyGlobalAndUniqueLocalIpv6(string? ip, bool expected)
    {
        GatewayNeighborTable.IsResolvable(ip).Should().Be(expected);
    }

    [Fact]
    public async Task ResolveMacAsync_ReadsTheTableOnceForSeveralLookups()
    {
        var ssh = new Mock<IGatewaySshService>();
        ssh.Setup(s => s.RunCommandAsync("ip -6 neigh show", It.IsAny<TimeSpan>()))
            .ReturnsAsync((true, NeighborOutput));
        var table = new GatewayNeighborTable(ssh.Object, NullLogger<GatewayNeighborTable>.Instance);

        (await table.ResolveMacAsync("fd00::1234")).Should().Be("aa:bb:cc:dd:ee:01");
        (await table.ResolveMacAsync("2001:db8:0:0::20")).Should().Be("aa:bb:cc:dd:ee:02");
        (await table.ResolveMacAsync("2001:db8::99")).Should().BeNull();

        ssh.Verify(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Once);
    }

    [Theory]
    [InlineData("192.0.2.10")]
    [InlineData("::ffff:192.0.2.10")]
    [InlineData("fe80::1")]
    public async Task ResolveMacAsync_NeverTouchesTheGatewayForAnIpv4Client(string ip)
    {
        var ssh = new Mock<IGatewaySshService>();
        var table = new GatewayNeighborTable(ssh.Object, NullLogger<GatewayNeighborTable>.Instance);

        (await table.ResolveMacAsync(ip)).Should().BeNull();

        ssh.Verify(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
    }

    [Fact]
    public async Task ResolveClientAsync_WithoutAConsole_ReturnsTheMacOnly()
    {
        var ssh = new Mock<IGatewaySshService>();
        ssh.Setup(s => s.RunCommandAsync("ip -6 neigh show", It.IsAny<TimeSpan>()))
            .ReturnsAsync((true, NeighborOutput));
        var table = new GatewayNeighborTable(ssh.Object, NullLogger<GatewayNeighborTable>.Instance);

        var resolved = await table.ResolveClientAsync("fd00::1234", console: null);

        resolved.Should().NotBeNull();
        resolved!.Value.Mac.Should().Be("aa:bb:cc:dd:ee:01");
        resolved.Value.Ipv4.Should().BeNull();
    }

    [Fact]
    public async Task ResolveMacAsync_AFailedRead_IsNotRetriedOnEveryCall()
    {
        var ssh = new Mock<IGatewaySshService>();
        ssh.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .ThrowsAsync(new InvalidOperationException("unreachable"));
        var table = new GatewayNeighborTable(ssh.Object, NullLogger<GatewayNeighborTable>.Instance);

        (await table.ResolveMacAsync("fd00::1234")).Should().BeNull();
        (await table.ResolveMacAsync("fd00::1234")).Should().BeNull();

        ssh.Verify(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Once);
    }

    [Theory]
    [InlineData("fd00::1234", "192.0.2.10", "192.0.2.10")]   // IPv6 source: ask WiFiman by the listed IPv4
    [InlineData("fd00::1234", null, "fd00::1234")]           // Nothing listed: the source itself
    [InlineData("fd00::1234", "fd00::1234", "fd00::1234")]   // A listed IPv6 is no better
    [InlineData("192.0.2.10", "192.0.2.99", "192.0.2.10")]   // IPv4 source: unchanged
    public void WiFiManIpFor_UsesTheListedIpv4ForAnIpv6Source(string clientIp, string? consoleIp, string expected)
    {
        ClientDashboardService.WiFiManIpFor(clientIp, consoleIp).Should().Be(expected);
    }
}
