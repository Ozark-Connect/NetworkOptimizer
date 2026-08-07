using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// On a load-balancing site an unpinned probe measures no single WAN, and a policy route is the
/// one thing that can say otherwise. It only counts when it steers EVERY destination for the
/// probing box - anything narrower says nothing about where a probe elsewhere leaves.
/// </summary>
public class TrafficRouteWanPinningTests
{
    private const string ProbeMac = "00:11:22:33:44:55";

    private static UniFiTrafficRouteResponse Route(
        string networkId, bool enabled = true, string target = "INTERNET",
        string? mac = ProbeMac, string deviceType = "CLIENT", bool killSwitch = false) => new()
        {
            Id = networkId + "-route",
            Description = "route",
            Enabled = enabled,
            MatchingTarget = target,
            NetworkId = networkId,
            KillSwitchEnabled = killSwitch,
            TargetDevices = new List<UniFiTrafficRouteTargetDevice>
            {
                new() { ClientMac = mac, Type = deviceType }
            }
        };

    [Fact]
    public void Pins_the_wan_of_a_blanket_internet_route_naming_the_device()
    {
        var pin = TrafficRouteWanPinning.ResolvePin(new[] { Route("wan-net-id") }, ProbeMac);

        pin.Should().NotBeNull();
        pin!.NetworkId.Should().Be("wan-net-id");
    }

    [Fact]
    public void Ignores_a_disabled_route()
    {
        TrafficRouteWanPinning.ResolvePin(new[] { Route("wan-net-id", enabled: false) }, ProbeMac)
            .Should().BeNull();
    }

    [Theory]
    [InlineData("IP")]
    [InlineData("DOMAIN")]
    [InlineData("REGION")]
    public void Ignores_a_route_that_steers_only_some_destinations(string target)
    {
        TrafficRouteWanPinning.ResolvePin(new[] { Route("wan-net-id", target: target) }, ProbeMac)
            .Should().BeNull();
    }

    [Fact]
    public void Ignores_a_route_aimed_at_another_device()
    {
        TrafficRouteWanPinning.ResolvePin(new[] { Route("wan-net-id", mac: "aa:bb:cc:dd:ee:ff") }, ProbeMac)
            .Should().BeNull();
    }

    [Fact]
    public void Accepts_an_all_clients_route()
    {
        var pin = TrafficRouteWanPinning.ResolvePin(
            new[] { Route("wan-net-id", mac: null, deviceType: "ALL_CLIENTS") }, ProbeMac);

        pin!.NetworkId.Should().Be("wan-net-id");
    }

    [Fact]
    public void Prefers_the_route_naming_the_device_over_a_blanket_all_clients_one()
    {
        var routes = new[]
        {
            Route("all-clients-net", mac: null, deviceType: "ALL_CLIENTS"),
            Route("named-net"),
        };

        TrafficRouteWanPinning.ResolvePin(routes, ProbeMac)!.NetworkId.Should().Be("named-net");
    }

    [Theory]
    [InlineData("00-11-22-33-44-55")]
    [InlineData("001122 334455")]
    public void Compares_macs_regardless_of_separator_or_case(string written)
    {
        TrafficRouteWanPinning.ResolvePin(new[] { Route("wan-net-id", mac: written) }, ProbeMac)
            .Should().NotBeNull();
    }

    [Fact]
    public void Reports_the_kill_switch_so_callers_know_a_failover_will_not_re_route()
    {
        TrafficRouteWanPinning.ResolvePin(new[] { Route("n", killSwitch: true) }, ProbeMac)!
            .KillSwitchEnabled.Should().BeTrue();
    }

    [Fact]
    public void Finds_nothing_for_a_device_with_no_mac()
    {
        TrafficRouteWanPinning.ResolvePin(new[] { Route("wan-net-id") }, null).Should().BeNull();
    }

    /// <summary>
    /// The console hands back bools and numbers as strings whenever it feels like it, so the DTO
    /// leans on the flexible converters rather than trusting the JSON types.
    /// </summary>
    [Fact]
    public void Reads_string_encoded_bools_the_console_may_send()
    {
        const string json = """
        [{"_id":"r1","description":"[TEST] Rig out DOCSIS","enabled":"true",
          "matching_target":"INTERNET","network_id":"wan-net-id",
          "kill_switch_enabled":"false",
          "target_devices":[{"client_mac":"00:11:22:33:44:55","type":"CLIENT"}]}]
        """;

        var routes = JsonSerializer.Deserialize<List<UniFiTrafficRouteResponse>>(json)!;

        routes[0].Enabled.Should().BeTrue();
        routes[0].KillSwitchEnabled.Should().BeFalse();
        TrafficRouteWanPinning.ResolvePin(routes, ProbeMac)!.NetworkId.Should().Be("wan-net-id");
    }
}
