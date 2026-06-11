using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// Tests for WAN interface name resolution used by Monitoring WAN throughput
/// (stat cards, charts, Live View). PPPoE gateways carry WAN traffic on the
/// ppp* tunnel, not the physical port, so SNMP counter lookups must use ppp*
/// (issue #669).
/// </summary>
public class WanInterfaceNameTests
{
    private static UniFiDeviceResponse Parse(string json) =>
        JsonSerializer.Deserialize<UniFiDeviceResponse>(json)!;

    [Fact]
    public void PlainEthernetWan_UsesPhysicalPort()
    {
        var device = Parse("""
            { "type": "ucg", "wan1": { "ifname": "eth4", "uplink_ifname": "eth4" } }
            """);
        UniFiDiscovery.GetWanInterfaceNames(device).Should().Equal("eth4");
    }

    [Fact]
    public void PppoeWan_UsesPppTunnel()
    {
        var device = Parse("""
            { "type": "ucg", "wan1": { "ifname": "eth4", "uplink_ifname": "ppp0" } }
            """);
        UniFiDiscovery.GetWanInterfaceNames(device).Should().Equal("ppp0");
    }

    [Fact]
    public void PppoeWan_MissingPhysicalIfname_UsesPppTunnel()
    {
        var device = Parse("""
            { "type": "ucg", "wan1": { "uplink_ifname": "ppp0" } }
            """);
        UniFiDiscovery.GetWanInterfaceNames(device).Should().Equal("ppp0");
    }

    [Fact]
    public void VlanTaggedWan_UsesPhysicalPort_NotSubInterface()
    {
        // VLAN sub-interfaces double-count on some kernels, so the physical port wins.
        var device = Parse("""
            { "type": "ucg", "wan1": { "ifname": "eth6", "uplink_ifname": "eth6.100" } }
            """);
        UniFiDiscovery.GetWanInterfaceNames(device).Should().Equal("eth6");
    }

    [Fact]
    public void MultiWan_ResolvesEachWanIndependently()
    {
        var device = Parse("""
            {
              "type": "ucg",
              "wan1": { "ifname": "eth4", "uplink_ifname": "ppp0" },
              "wan2": { "ifname": "eth5", "uplink_ifname": "eth5" }
            }
            """);
        UniFiDiscovery.GetWanInterfaceNames(device).Should().Equal("ppp0", "eth5");
    }

    [Fact]
    public void NoWanObjects_FallsBackToPortTableUplinks()
    {
        var device = Parse("""
            {
              "type": "usw",
              "port_table": [
                { "port_idx": 1, "ifname": "eth0", "is_uplink": true },
                { "port_idx": 2, "ifname": "eth1", "is_uplink": false }
              ]
            }
            """);
        UniFiDiscovery.GetWanInterfaceNames(device).Should().Equal("eth0");
    }

    [Fact]
    public void WanObjectsWithoutIfnames_FallsBackToPortTableUplinks()
    {
        var device = Parse("""
            {
              "type": "ucg",
              "wan1": { "name": "WAN1" },
              "port_table": [
                { "port_idx": 4, "ifname": "eth4", "is_uplink": true }
              ]
            }
            """);
        UniFiDiscovery.GetWanInterfaceNames(device).Should().Equal("eth4");
    }

    [Fact]
    public void DuplicateWanKeys_AreDeduplicated()
    {
        // Some firmware exposes both "wan" and "wan1" pointing at the same interface.
        var device = Parse("""
            {
              "type": "ucg",
              "wan":  { "ifname": "eth4", "uplink_ifname": "ppp0" },
              "wan1": { "ifname": "eth4", "uplink_ifname": "ppp0" }
            }
            """);
        UniFiDiscovery.GetWanInterfaceNames(device).Should().Equal("ppp0");
    }
}
