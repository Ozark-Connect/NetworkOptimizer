using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// A UniFi Building Bridge pair is one listed device with the other unit nested in its peer_ubb.
/// The far building's switch uplinks to the nested unit's MAC, so a device list without it has a
/// wireless span with one end and a subtree hanging off nothing.
/// </summary>
public class BuildingBridgePeerTests
{
    private const string SwitchMac = "aa:bb:cc:00:00:01";
    private const string ListedMac = "aa:bb:cc:00:00:10";
    private const string PeerMac = "aa:bb:cc:00:00:20";

    private static List<UniFiDeviceResponse> Parse(string json) =>
        JsonSerializer.Deserialize<List<UniFiDeviceResponse>>(json)!;

    // The listed unit is wired to the switch; the nested unit is the wireless end of the span.
    private const string PairJson = """
        [
          { "type": "usw", "model": "USL8LP", "mac": "aa:bb:cc:00:00:01" },
          {
            "type": "ubb", "model": "UBB", "mac": "aa:bb:cc:00:00:10", "name": "HQ side",
            "uplink": { "type": "wire", "uplink_mac": "aa:bb:cc:00:00:01", "uplink_remote_port": 17, "port_idx": 1, "speed": 1000 },
            "peer_ubb": {
              "type": "ubb", "model": "UBB", "mac": "aa:bb:cc:00:00:20", "name": "Far side", "state": 1, "ip": "192.0.2.20",
              "uplink": {
                "type": "wireless", "uplink_mac": "aa:bb:cc:00:00:10", "radio": "na", "channel": 100,
                "signal": -60, "tx_rate": 274477, "rx_rate": 334799, "tx_bytes": "4076657960", "rx_bytes": 5753964353
              },
              "active_sta_table": [
                { "name": "wlan0", "radio": "ad", "channel": 4, "signal": -73, "tx_rate": 336875, "rx_rate": 311000, "active": true, "mac": "aa:bb:cc:00:00:10" },
                { "name": "ath0", "radio": "na", "channel": 100, "signal": -60, "tx_rate": 274477, "rx_rate": 334799, "active": false, "mac": "aa:bb:cc:00:00:10" }
              ]
            }
          }
        ]
        """;

    [Fact]
    public void BuildingBridgePeers_SurfacesTheNestedUnit()
    {
        var peers = UniFiDiscovery.BuildingBridgePeers(Parse(PairJson));

        peers.Should().ContainSingle().Which.Mac.Should().Be(PeerMac);
    }

    [Fact]
    public void BuildingBridgePeers_LeavesOutAUnitTheConsoleAlsoLists()
    {
        var devices = Parse(PairJson);
        devices.Add(new UniFiDeviceResponse { Type = "ubb", Model = "UBB", Mac = PeerMac.ToUpperInvariant() });

        UniFiDiscovery.BuildingBridgePeers(devices).Should().BeEmpty();
    }

    [Fact]
    public void BuildingBridgePeers_IsEmptyWithoutAPair()
    {
        UniFiDiscovery.BuildingBridgePeers(Parse("""[{ "type": "usw", "mac": "aa:bb:cc:00:00:01" }]"""))
            .Should().BeEmpty();
    }

    [Fact]
    public void MapBuildingBridgePeer_IsABuildingBridgeUplinkedToItsSibling()
    {
        var peer = UniFiDiscovery.BuildingBridgePeers(Parse(PairJson))[0];

        var device = UniFiDiscovery.MapBuildingBridgePeer(peer);

        device.Type.Should().Be(DeviceType.BuildingBridge);
        device.Mac.Should().Be(PeerMac);
        device.Name.Should().Be("Far side");
        device.UplinkMac.Should().Be(ListedMac);
        device.UplinkType.Should().Be("wireless");
        device.State.Should().Be(1);
    }

    [Fact]
    public void WirelessUnit_TakesItsBandAndRatesFromTheActiveLink()
    {
        // The uplink block describes the 5 GHz fallback; the 60 GHz link is the one flagged
        // active, and it is what the console's own link_capacity follows.
        var peer = UniFiDiscovery.BuildingBridgePeers(Parse(PairJson))[0];

        var device = UniFiDiscovery.MapBuildingBridgePeer(peer);

        device.UplinkRadioBand.Should().Be("ad");
        device.UplinkChannel.Should().Be(4);
        device.UplinkSignalDbm.Should().Be(-73);
        device.UplinkTxRateKbps.Should().Be(336875);
        device.UplinkRxRateKbps.Should().Be(311000);
        device.UplinkSpeedMbps.Should().Be(336);
    }

    [Fact]
    public void WirelessUnit_KeepsTheUplinkBlockWhenNoLinkIsFlaggedActive()
    {
        var peer = UniFiDiscovery.BuildingBridgePeers(Parse(PairJson))[0];
        foreach (var l in peer.ActiveStaTable!) l.Active = false;

        var device = UniFiDiscovery.MapBuildingBridgePeer(peer);

        device.UplinkRadioBand.Should().Be("na");
        device.UplinkChannel.Should().Be(100);
        device.UplinkTxRateKbps.Should().Be(274477);
    }

    [Fact]
    public void UplinkByteCounters_ParseWhetherStringOrNumber()
    {
        var peer = UniFiDiscovery.BuildingBridgePeers(Parse(PairJson))[0];

        peer.Uplink!.TxBytes.Should().Be(4076657960);
        peer.Uplink.RxBytes.Should().Be(5753964353);
    }
}
