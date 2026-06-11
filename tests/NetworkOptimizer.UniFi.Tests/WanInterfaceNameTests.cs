using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// Tests for WAN interface name resolution used by Monitoring WAN throughput
/// (stat cards, charts, Live View). By design this resolves the PRIMARY WAN
/// only - the surrounding ISP / transit latency cards are measured for that
/// one connection. The primary WAN may be plain ethernet, VLAN-tagged, PPPoE
/// (counters live on the ppp* tunnel, issue #669), or a GRE-tunneled cellular
/// WAN with no physical port.
/// </summary>
public class WanInterfaceNameTests
{
    private static UniFiDeviceResponse Parse(string json) =>
        JsonSerializer.Deserialize<UniFiDeviceResponse>(json)!;

    // ─── Single-WAN gateways, one per connection type ───

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
    public void CellularGreWan_UsesGreTunnel()
    {
        // UniFi 5G Max reaches the gateway over a LAN GRE tunnel; UniFi reports
        // the tunnel as both ifname and uplink_ifname.
        var device = Parse("""
            { "type": "ucg", "wan1": { "type": "wireless_5g", "ifname": "gre1", "uplink_ifname": "gre1" } }
            """);
        UniFiDiscovery.GetWanInterfaceNames(device).Should().Equal("gre1");
    }

    // ─── Primary selection via the gateway uplink object (firmware shapes vary) ───

    [Fact]
    public void UplinkObject_NameCarriesVlanInterface_ResolvesPrimaryPhysicalPort()
    {
        // Firmware shape A: uplink.name holds the active WAN's logical interface,
        // ifname/uplink_ifname absent. Multi-WAN site; only the primary resolves.
        var device = Parse("""
            {
              "type": "ucg",
              "uplink": { "name": "eth6.100", "type": "wire", "up": true },
              "wan1": { "ifname": "eth6", "uplink_ifname": "eth6.100", "up": true },
              "wan2": { "ifname": "eth0", "uplink_ifname": "eth0", "up": true },
              "wan3": { "type": "wireless_5g", "ifname": "gre1", "uplink_ifname": "gre1", "up": true }
            }
            """);
        UniFiDiscovery.GetWanInterfaceNames(device).Should().Equal("eth6");
    }

    [Fact]
    public void UplinkObject_UplinkIfnameCarriesPppTunnel_ResolvesPppTunnel()
    {
        // Firmware shape B (seen on a PPPoE UCG-Fiber): uplink.name is a bridge,
        // uplink.uplink_ifname holds the WAN interface.
        var device = Parse("""
            {
              "type": "ucg",
              "uplink": { "name": "br0", "uplink_ifname": "ppp0", "type": "wire", "up": true },
              "wan1": { "ifname": "eth4", "uplink_ifname": "ppp0", "up": true }
            }
            """);
        UniFiDiscovery.GetWanInterfaceNames(device).Should().Equal("ppp0");
    }

    [Fact]
    public void UplinkObject_GrePrimary_ResolvesGreTunnel_NotWan1()
    {
        // Cellular as the PRIMARY WAN: the uplink object points at gre1 even
        // though an ethernet WAN exists as wan1.
        var device = Parse("""
            {
              "type": "ucg",
              "uplink": { "name": "gre1", "type": "wire", "up": true },
              "wan1": { "ifname": "eth4", "uplink_ifname": "eth4", "up": false },
              "wan2": { "type": "wireless_5g", "ifname": "gre1", "uplink_ifname": "gre1", "up": true }
            }
            """);
        UniFiDiscovery.GetWanInterfaceNames(device).Should().Equal("gre1");
    }

    [Fact]
    public void UplinkObject_MatchesByPhysicalIfname()
    {
        // uplink references the physical port name; wan matched via ifname.
        var device = Parse("""
            {
              "type": "ucg",
              "uplink": { "name": "eth4", "type": "wire", "up": true },
              "wan1": { "ifname": "eth4", "uplink_ifname": "ppp0", "up": true }
            }
            """);
        UniFiDiscovery.GetWanInterfaceNames(device).Should().Equal("ppp0");
    }

    [Fact]
    public void UplinkObject_NoMatchingWan_FallsThroughToPortTable()
    {
        // uplink names something that isn't a WAN (defensive): fall through.
        var device = Parse("""
            {
              "type": "ucg",
              "uplink": { "name": "br0", "type": "wire", "up": true },
              "wan1": { "ifname": "eth4", "uplink_ifname": "ppp0", "port_idx": 4 },
              "port_table": [
                { "port_idx": 4, "ifname": "eth4", "is_uplink": true }
              ]
            }
            """);
        UniFiDiscovery.GetWanInterfaceNames(device).Should().Equal("ppp0");
    }

    // ─── Primary selection via port_table is_uplink (no usable uplink object) ───

    [Fact]
    public void PppoeWan_UplinkPortFlagged_RemapsToPppTunnel()
    {
        // is_uplink points at the physical port; the counters live on ppp0.
        var device = Parse("""
            {
              "type": "ucg",
              "wan1": { "ifname": "eth4", "uplink_ifname": "ppp0", "port_idx": 4 },
              "port_table": [
                { "port_idx": 4, "ifname": "eth4", "is_uplink": true }
              ]
            }
            """);
        UniFiDiscovery.GetWanInterfaceNames(device).Should().Equal("ppp0");
    }

    [Fact]
    public void FlaggedUplinkPort_MatchesWanByPortIdx_WhenIfnameMissing()
    {
        var device = Parse("""
            {
              "type": "ucg",
              "wan1": { "uplink_ifname": "ppp0", "port_idx": 4 },
              "port_table": [
                { "port_idx": 4, "ifname": "eth4", "is_uplink": true }
              ]
            }
            """);
        UniFiDiscovery.GetWanInterfaceNames(device).Should().Equal("ppp0");
    }

    [Fact]
    public void MultiWan_FlaggedUplink_ResolvesOnlyThePrimaryWan()
    {
        // Primary WAN only, by design: the WAN Live View / overview stats sit
        // alongside ISP / transit latency measured for one connection.
        var device = Parse("""
            {
              "type": "ucg",
              "wan1": { "ifname": "eth6", "uplink_ifname": "eth6.100", "port_idx": 7 },
              "wan2": { "ifname": "eth0", "uplink_ifname": "eth0", "port_idx": 1 },
              "wan3": { "type": "wireless_5g", "ifname": "gre1", "uplink_ifname": "gre1" },
              "port_table": [
                { "port_idx": 1, "ifname": "eth0", "is_uplink": false },
                { "port_idx": 7, "ifname": "eth6", "is_uplink": true }
              ]
            }
            """);
        UniFiDiscovery.GetWanInterfaceNames(device).Should().Equal("eth6");
    }

    [Fact]
    public void FlaggedUplinkPort_NoMatchingWanObject_UsesPortIfname()
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
    public void NonGatewayDevice_UsesPortTableUplink()
    {
        // Switches/APs have no wan objects; their uplink port resolves as before.
        var device = Parse("""
            {
              "type": "usw",
              "uplink": { "name": "Port 24", "port_idx": 24, "type": "wire", "up": true },
              "port_table": [
                { "port_idx": 1, "ifname": "eth0", "is_uplink": true },
                { "port_idx": 2, "ifname": "eth1", "is_uplink": false }
              ]
            }
            """);
        UniFiDiscovery.GetWanInterfaceNames(device).Should().Equal("eth0");
    }

    // ─── Last-resort fallback: wan objects only (seen on PPPoE gateways) ───

    [Fact]
    public void MultiWan_NoUplinkSignals_ResolvesFirstWan()
    {
        var device = Parse("""
            {
              "type": "ucg",
              "wan1": { "ifname": "eth4", "uplink_ifname": "ppp0", "up": true },
              "wan2": { "ifname": "eth5", "uplink_ifname": "eth5", "up": true }
            }
            """);
        UniFiDiscovery.GetWanInterfaceNames(device).Should().Equal("ppp0");
    }

    [Fact]
    public void MultiWan_NoUplinkSignals_PrefersWanThatIsUp()
    {
        // GRE cellular as the only WAN that is up; wan1 ethernet is down.
        var device = Parse("""
            {
              "type": "ucg",
              "wan1": { "ifname": "eth4", "uplink_ifname": "eth4", "up": false },
              "wan2": { "type": "wireless_5g", "ifname": "gre1", "uplink_ifname": "gre1", "up": true }
            }
            """);
        UniFiDiscovery.GetWanInterfaceNames(device).Should().Equal("gre1");
    }

    [Fact]
    public void DuplicateWanKeys_ResolveToSingleName()
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

    [Fact]
    public void NoWanData_ReturnsEmpty()
    {
        var device = Parse("""{ "type": "ucg" }""");
        UniFiDiscovery.GetWanInterfaceNames(device).Should().BeEmpty();
    }
}
