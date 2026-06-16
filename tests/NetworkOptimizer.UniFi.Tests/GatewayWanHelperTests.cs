using System.Text.Json;
using FluentAssertions;
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
}
