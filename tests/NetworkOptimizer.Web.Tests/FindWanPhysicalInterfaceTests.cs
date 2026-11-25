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
    private const string Gateway = """
        {
          "type": "udm",
          "mac": "aa:bb:cc:dd:ee:01",
          "wan1": { "uplink_ifname": "ppp0", "ifname": "eth6" },
          "wan2": { "uplink_ifname": "ppp1", "name": "eth4" },
          "wan3": { "uplink_ifname": "ppp2", "port_idx": 3, "name": "Internet" },
          "port_table": [{ "port_idx": 3, "ifname": "eth0" }]
        }
        """;

    private const string Switch = """{ "type": "usw", "mac": "aa:bb:cc:dd:ee:03" }""";

    private static List<UniFiDeviceResponse> Devices(params string[] devices) =>
        JsonSerializer.Deserialize<List<UniFiDeviceResponse>>($"[{string.Join(",", devices)}]")!;

    [Theory]
    [InlineData("ppp0", "eth6")]
    [InlineData("ppp1", "eth4")]
    [InlineData("ppp2", "eth0")]
    [InlineData("ppp9", null)]
    public void FindWanPhysicalInterface_MatchesTheRequestedUplink(string uplinkInterface, string? expected)
    {
        var devices = Devices(Switch, Gateway);

        UniFiConnectionService.FindWanPhysicalInterface(devices, uplinkInterface).Should().Be(expected);
    }

    [Fact]
    public void FindWanPhysicalInterface_NoGateway_ReturnsNull()
    {
        UniFiConnectionService.FindWanPhysicalInterface(Devices(Switch), "ppp0").Should().BeNull();
    }

    [Fact]
    public void FindWanGateway_ExpressInApMode_IsNotTheGateway()
    {
        var express = """{ "type": "udm", "mac": "aa:bb:cc:dd:ee:04", "device_mode_override": "mesh" }""";

        UniFiConnectionService.FindWanGateway(Devices(express, Gateway))!.Mac.Should().Be("aa:bb:cc:dd:ee:01");
    }
}
