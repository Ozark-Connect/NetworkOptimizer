using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.UniFi.Tests.Fixtures;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// A Client WAN Test from a client on a gateway-terminated VPN. The client is in no client list, so the LAN walk finds
/// nothing ("not found in network topology"); its test runs in over the WAN, through the gateway, and back out.
/// Tailscale keeps today's behavior: whether a tailnet client's test crossed this site is not recorded.
/// </summary>
public class RemoteWanClientPathTests
{
    private readonly NetworkPathAnalyzer _analyzer;

    public RemoteWanClientPathTests()
    {
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);
        _analyzer = new NetworkPathAnalyzer(
            new Mock<IUniFiClientProvider>().Object, new MemoryCache(new MemoryCacheOptions()), loggerFactory.Object);
    }

    private NetworkPath? Build(string clientIp) =>
        _analyzer.BuildRemoteWanClientPath(
            clientIp, NetworkTestData.CreateBasicTopology(), new Dictionary<string, UniFiDeviceResponse>(), resolvedWanGroup: null);

    [Fact]
    public void A_teleport_client_gets_wan_then_gateway_then_teleport()
    {
        // Teleport hands out 192.168 addresses on no network the site defines.
        var path = Build("192.168.2.3");

        path.Should().NotBeNull();
        path!.IsValid.Should().BeTrue();
        path.IsExternalPath.Should().BeTrue();
        path.DestinationMac.Should().BeNullOrEmpty("a remote client's MAC is unknown; the gateway's must not stand in for it");
        path.Hops.Select(h => h.Type).Should().Equal(HopType.Wan, HopType.Gateway, HopType.Teleport);
        path.Hops[1].DeviceMac.Should().Be(NetworkTestData.GatewayMac);
        path.Hops[2].DeviceIp.Should().Be("192.168.2.3");
        path.Hops.Select(h => h.Order).Should().BeInAscendingOrder();
    }

    [Fact]
    public void A_tailscale_client_is_left_to_the_lan_walk()
    {
        Build("100.97.85.114").Should().BeNull();
    }

    [Fact]
    public void A_local_address_is_left_to_the_lan_walk()
    {
        // In the site's own network: the LAN walk finds it, or reports it not found as before.
        Build("192.0.2.250").Should().BeNull();
    }

    [Fact]
    public void A_public_address_is_left_to_the_lan_walk()
    {
        Build("203.0.113.40").Should().BeNull();
    }
}
