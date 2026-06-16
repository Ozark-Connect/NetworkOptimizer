using System.Linq;
using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.UniFi;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

public class GatewayWanHelperTests
{
    [Theory]
    [InlineData(1, "WAN")]
    [InlineData(2, "WAN2")]
    [InlineData(4, "WAN4")]
    public void WanNetworkGroup_follows_unifi_convention(int index, string expected)
        => GatewayWanHelper.WanNetworkGroup(index).Should().Be(expected);

    [Theory]
    [InlineData(1, "wan")]
    [InlineData(2, "wan2")]
    [InlineData(4, "wan4")]
    public void WanInterfaceKey_follows_unifi_convention(int index, string expected)
        => GatewayWanHelper.WanInterfaceKey(index).Should().Be(expected);

    [Theory]
    [InlineData("wan", "WAN")]
    [InlineData("wan1", "WAN")]
    [InlineData("wan2", "WAN2")]
    [InlineData("WAN3", "WAN3")]
    public void WanNetworkGroupFromKey_maps_primary_and_uppercases(string key, string expected)
        => GatewayWanHelper.WanNetworkGroupFromKey(key).Should().Be(expected);

    [Fact]
    public void BuildNetworkGroupByIfname_maps_ifname_to_networkgroup_case_insensitively()
    {
        var eo = JsonDocument.Parse("""
            [ { "ifname": "eth6", "networkgroup": "WAN" },
              { "ifname": "eth1", "networkgroup": "WAN2" } ]
            """).RootElement;

        var map = GatewayWanHelper.BuildNetworkGroupByIfname(eo);

        map["eth6"].Should().Be("WAN");
        map["ETH1"].Should().Be("WAN2");
    }

    [Fact]
    public void BuildNetworkGroupByIfname_skips_incomplete_entries()
    {
        var eo = JsonDocument.Parse("""
            [ { "ifname": "eth6" }, { "networkgroup": "WAN2" }, { "ifname": "eth1", "networkgroup": "WAN2" } ]
            """).RootElement;

        var map = GatewayWanHelper.BuildNetworkGroupByIfname(eo);

        map.Should().ContainSingle().Which.Key.Should().Be("eth1");
    }

    [Fact]
    public void BuildNetworkGroupByIfname_returns_empty_for_absent_or_non_array()
    {
        GatewayWanHelper.BuildNetworkGroupByIfname(default).Should().BeEmpty();
        GatewayWanHelper.BuildNetworkGroupByIfname(JsonDocument.Parse("{}").RootElement).Should().BeEmpty();
    }

    [Theory]
    [InlineData("wan", "wan")]
    [InlineData("wan1", "wan")]
    [InlineData("wan2", "wan2")]
    [InlineData("WAN3", "wan3")]
    public void WanInterfaceKeyFromKey_lowercases_and_maps_primary(string key, string expected)
        => GatewayWanHelper.WanInterfaceKeyFromKey(key).Should().Be(expected);

    // ─── EnumerateWanInterfaces: typed field extraction from raw device JSON.
    // The physical (ifname) and data-path (uplink_ifname) extraction here is the
    // PPPoE/VLAN regression surface for the flow-map and upstream parsers, so it is
    // locked per WAN type (we have no PPPoE site to test against live). ───

    private static JsonElement Device(string body) => JsonDocument.Parse(body).RootElement;

    [Fact]
    public void Enumerate_Pppoe_keeps_physical_eth_and_datapath_ppp0()
    {
        var wan = GatewayWanHelper.EnumerateWanInterfaces(
            Device("""{ "wan1": { "ifname": "eth4", "uplink_ifname": "ppp0", "ip": "203.0.113.5" } }""")).Single();

        wan.Key.Should().Be("wan1");
        wan.IfName.Should().Be("eth4");
        wan.UplinkIfName.Should().Be("ppp0");
        // Counter for PPPoE must remain the tunnel, not the physical port (PR #769).
        NetworkUtilities.PreferredWanCounterInterface(wan.IfName, wan.UplinkIfName).Should().Be("ppp0");
    }

    [Fact]
    public void Enumerate_VlanTagged_keeps_physical_parent_and_datapath_subinterface()
    {
        var wan = GatewayWanHelper.EnumerateWanInterfaces(
            Device("""{ "wan1": { "ifname": "eth6", "uplink_ifname": "eth6.100", "speed": 2500 } }""")).Single();

        wan.IfName.Should().Be("eth6");
        wan.UplinkIfName.Should().Be("eth6.100");
        wan.Speed.Should().Be(2500);
        NetworkUtilities.PreferredWanCounterInterface(wan.IfName, wan.UplinkIfName).Should().Be("eth6");
    }

    [Fact]
    public void Enumerate_PlainEthernet_and_Gre_resolve_their_own_interface()
    {
        GatewayWanHelper.EnumerateWanInterfaces(
            Device("""{ "wan1": { "ifname": "eth4", "uplink_ifname": "eth4" } }""")).Single()
            .UplinkIfName.Should().Be("eth4");

        GatewayWanHelper.EnumerateWanInterfaces(
            Device("""{ "wan1": { "ifname": "gre1", "uplink_ifname": "gre1" } }""")).Single()
            .UplinkIfName.Should().Be("gre1");
    }

    [Fact]
    public void Enumerate_speed_tolerates_string_values()
    {
        // FlexibleIntConverter: the UniFi API sometimes serializes ints as strings.
        var wan = GatewayWanHelper.EnumerateWanInterfaces(
            Device("""{ "wan1": { "ifname": "eth4", "uplink_ifname": "ppp0", "speed": "1000" } }""")).Single();

        wan.Speed.Should().Be(1000);
        wan.UplinkIfName.Should().Be("ppp0");
    }

    [Fact]
    public void Enumerate_returns_all_present_wans_with_keys_and_skips_gaps()
    {
        var wans = GatewayWanHelper.EnumerateWanInterfaces(
            Device("""
                { "wan1": { "ifname": "eth6", "uplink_ifname": "eth6.100" },
                  "wan4": { "ifname": "eth1", "uplink_ifname": "eth1" } }
                """)).ToList();

        wans.Select(w => w.Key).Should().Equal("wan1", "wan4");
        wans[1].IfName.Should().Be("eth1");
    }

    [Fact]
    public void Enumerate_ignores_absent_and_non_object_wan_entries()
    {
        GatewayWanHelper.EnumerateWanInterfaces(Device("""{ "type": "ucg" }""")).Should().BeEmpty();
        GatewayWanHelper.EnumerateWanInterfaces(Device("""{ "wan1": "not-an-object" }""")).Should().BeEmpty();
    }
}
