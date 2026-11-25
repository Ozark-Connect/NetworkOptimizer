using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// <see cref="UniFiConnectionService.FindWanPhysicalInterface"/> and the gateway pick it shares
/// with the other WAN lookups (<see cref="UniFiConnectionService.FindWanGateway"/>).
/// </summary>
public class FindWanPhysicalInterfaceTests
{
    private const string ActiveGateway = """
        {
          "type": "udm",
          "mac": "aa:bb:cc:dd:ee:01",
          "wan1": { "uplink_ifname": "ppp0", "ifname": "eth6" },
          "wan2": { "uplink_ifname": "ppp1", "name": "eth4" },
          "wan3": { "uplink_ifname": "ppp2", "port_idx": 3, "name": "Internet" },
          "port_table": [{ "port_idx": 3, "ifname": "eth0" }]
        }
        """;

    private const string ShadowGateway = """
        {
          "type": "udm",
          "mac": "aa:bb:cc:dd:ee:02",
          "device_mode_override": "managed_shadow_gateway",
          "wan1": { "uplink_ifname": "ppp0", "ifname": "eth9" }
        }
        """;

    private static List<UniFiDeviceResponse> Devices(params string[] devices) =>
        JsonSerializer.Deserialize<List<UniFiDeviceResponse>>($"[{string.Join(",", devices)}]")!;

    [Theory]
    [InlineData("ppp0", "eth6")]
    [InlineData("ppp1", "eth4")]
    [InlineData("ppp2", "eth0")]
    [InlineData("ppp9", null)]
    public void FindWanPhysicalInterface_MatchesTheRequestedUplink(string uplinkInterface, string? expected)
    {
        var devices = Devices(ShadowGateway, ActiveGateway);

        UniFiConnectionService.FindWanPhysicalInterface(devices, uplinkInterface).Should().Be(expected);
    }

    [Fact]
    public void FindWanPhysicalInterface_NoGateway_ReturnsNull()
    {
        var devices = Devices("""{ "type": "usw", "mac": "aa:bb:cc:dd:ee:03" }""");

        UniFiConnectionService.FindWanPhysicalInterface(devices, "ppp0").Should().BeNull();
    }

    [Fact]
    public void FindWanGateway_HaPair_PrefersTheActiveGatewayInEitherOrder()
    {
        UniFiConnectionService.FindWanGateway(Devices(ShadowGateway, ActiveGateway))!.Mac
            .Should().Be("aa:bb:cc:dd:ee:01");
        UniFiConnectionService.FindWanGateway(Devices(ActiveGateway, ShadowGateway))!.Mac
            .Should().Be("aa:bb:cc:dd:ee:01");
    }

    [Fact]
    public void FindWanGateway_OnlyShadowGateway_StillReturnsIt()
    {
        UniFiConnectionService.FindWanGateway(Devices(ShadowGateway))!.Mac.Should().Be("aa:bb:cc:dd:ee:02");
    }
}
