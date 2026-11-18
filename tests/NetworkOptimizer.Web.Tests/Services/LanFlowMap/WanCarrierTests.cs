using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.LanFlowMap;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services.LanFlowMap;

/// <summary>
/// Which cellular modem the LAN maps draw a WAN from, and which device node a modem's signal
/// lands on. A modem is only ever named on proof; anything less leaves the WAN on the gateway.
/// </summary>
public class WanCarrierTests
{
    private const string GwMac = "aa:bb:cc:00:00:01";
    private const string SwitchMac = "aa:bb:cc:00:00:02";
    private const string ModemMac = "aa:bb:cc:00:00:03";

    private static UniFiDeviceResponse Device(string json) =>
        JsonSerializer.Deserialize<UniFiDeviceResponse>(json)!;

    // A gateway whose WAN3 is either a GRE tunnel (no port) or on port 5.
    private static UniFiDeviceResponse Gateway(string wan3Type, int? wan3Port, string port5Network = "wan3") => Device($$"""
        {
          "mac": "{{GwMac}}", "type": "uxg", "model": "UXGA6AA",
          "wan1": { "type": "ethernet", "ifname": "eth6", "port_idx": 7, "up": true },
          "wan3": { "type": "{{wan3Type}}", "ifname": "{{(wan3Port == null ? "gre1" : "eth4")}}", "port_idx": {{(wan3Port?.ToString() ?? "null")}}, "up": true },
          "port_table": [
            { "port_idx": 5, "name": "Port 5", "network_name": "{{port5Network}}" },
            { "port_idx": 7, "name": "Port 7", "network_name": "wan" }
          ]
        }
        """);

    private static UniFiDeviceResponse Modem(string uplinkMac, int uplinkPort) => Device($$"""
        {
          "mac": "{{ModemMac}}", "type": "umbb", "model": "UMBBE630", "shortname": "U5GMAX",
          "uplink": { "uplink_mac": "{{uplinkMac}}", "uplink_remote_port": {{uplinkPort}}, "type": "wire" }
        }
        """);

    private static Dictionary<string, UniFiDeviceResponse> Raw(params UniFiDeviceResponse[] devices) =>
        devices.ToDictionary(d => d.Mac, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void TunneledWan_IsCarriedByTheModemItsConfigNames()
    {
        var raw = Raw(Gateway("wireless_5g", null), Modem(SwitchMac, 7));

        var (via, tunnel, kind) = LanFlowMapService.ResolveWanCarrier("wan3", GwMac, ModemMac.ToUpperInvariant(), raw);

        via.Should().Be(ModemMac);
        tunnel.Should().BeTrue();
        kind.Should().Be("5G");
    }

    [Fact]
    public void DirectModem_OnTheWansGatewayPort_CarriesIt()
    {
        var raw = Raw(Gateway("wireless_lte", 5), Modem(GwMac, 5));

        var (via, tunnel, kind) = LanFlowMapService.ResolveWanCarrier("wan3", GwMac, null, raw);

        via.Should().Be(ModemMac);
        tunnel.Should().BeFalse();
        kind.Should().Be("LTE");
    }

    [Fact]
    public void DirectModem_OnAPortThatIsNotThisWans_IsNotNamed()
    {
        // The modem uplinks to the gateway, but on port 7, which is WAN1's.
        var raw = Raw(Gateway("wireless_5g", 5), Modem(GwMac, 7));

        LanFlowMapService.ResolveWanCarrier("wan3", GwMac, null, raw).ViaMac.Should().BeNull();
    }

    [Fact]
    public void DirectModem_OnAGatewayLanPort_IsNotNamed()
    {
        // The wan3 object claims port 5, but the port table says port 5 carries a LAN.
        var raw = Raw(Gateway("wireless_5g", 5, port5Network: "lan"), Modem(GwMac, 5));

        LanFlowMapService.ResolveWanCarrier("wan3", GwMac, null, raw).ViaMac.Should().BeNull();
    }

    [Fact]
    public void CellularWan_WithNoModemToDrawFrom_StillGetsItsBadge()
    {
        // A built-in modem (UDR-5G-Max) or an unadopted one: nothing on the device list.
        var raw = Raw(Gateway("wireless_5g", 5));

        var (via, _, kind) = LanFlowMapService.ResolveWanCarrier("wan3", GwMac, null, raw);

        via.Should().BeNull();
        kind.Should().Be("5G");
    }

    [Fact]
    public void WiredWan_IsNeitherCarriedNorBadged()
    {
        var raw = Raw(Gateway("wireless_5g", null), Modem(SwitchMac, 7));

        var (via, _, kind) = LanFlowMapService.ResolveWanCarrier("wan", GwMac, null, raw);

        via.Should().BeNull();
        kind.Should().BeNull();
    }

    [Fact]
    public void GatewayModem_LandsOnTheGateway_WhateverItsHostSays()
    {
        var nodes = new List<(string, LanNodeKind, string?)>
        {
            ("dev-gw", LanNodeKind.Gateway, "192.0.2.1"),
            ("dev-modem", LanNodeKind.Switch, "192.0.2.5"),
        };
        var modems = new[] { (1, (string?)"unifi-gateway", (string?)"gateway.example.lan") };

        LanFlowMapService.MatchModemNodes(modems, nodes).Should().ContainKey(1).WhoseValue.Should().Be("dev-gw");
    }

    [Fact]
    public void GatewayModem_OnAnHaPair_PrefersTheGatewayItsHostNames()
    {
        var nodes = new List<(string, LanNodeKind, string?)>
        {
            ("dev-gw-a", LanNodeKind.Gateway, "192.0.2.1"),
            ("dev-gw-b", LanNodeKind.Gateway, "192.0.2.2"),
        };
        var modems = new[] { (1, (string?)"unifi-gateway", (string?)"192.0.2.2") };

        LanFlowMapService.MatchModemNodes(modems, nodes)[1].Should().Be("dev-gw-b");
    }

    [Fact]
    public void OtherModems_StillMatchByHostIp()
    {
        var nodes = new List<(string, LanNodeKind, string?)>
        {
            ("dev-gw", LanNodeKind.Gateway, "192.0.2.1"),
            ("dev-modem", LanNodeKind.Switch, "192.0.2.5"),
        };
        var modems = new[]
        {
            (1, (string?)"qmicli", (string?)" 192.0.2.5 "),
            (2, (string?)"qmicli", (string?)"modem.example.lan"),
        };

        var result = LanFlowMapService.MatchModemNodes(modems, nodes);

        result[1].Should().Be("dev-modem");
        result.Should().NotContainKey(2);
    }

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ModeAt_IsFiveG_WhileThePollReadsNr()
    {
        // 5G NSA writes an LTE anchor point and an NR point on each poll.
        var polls = new List<(DateTime, bool)> { (T0, false), (T0, true), (T0.AddMinutes(2), false), (T0.AddMinutes(2), true) };

        LanFlowMapService.CellularModeAt(polls, T0.AddMinutes(3)).Should().Be("5G");
    }

    [Fact]
    public void ModeAt_IsLte_OnceNrDropsOut()
    {
        var polls = new List<(DateTime, bool)> { (T0, false), (T0, true), (T0.AddMinutes(2), false), (T0.AddMinutes(4), false) };

        LanFlowMapService.CellularModeAt(polls, T0.AddMinutes(1)).Should().Be("5G");
        LanFlowMapService.CellularModeAt(polls, T0.AddMinutes(5)).Should().Be("LTE");
    }

    [Fact]
    public void ModeAt_IsUnknown_BeforeTheFirstPollOrOnceStale()
    {
        var polls = new List<(DateTime, bool)> { (T0, true) };

        LanFlowMapService.CellularModeAt(polls, T0.AddMinutes(-1)).Should().BeNull();
        LanFlowMapService.CellularModeAt(polls, T0.AddDays(1)).Should().BeNull();
    }
}
