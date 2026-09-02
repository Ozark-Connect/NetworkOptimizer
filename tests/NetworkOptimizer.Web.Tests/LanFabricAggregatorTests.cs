using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// Direction pins for the device aggregate: RateInBps carries the device's uploads (toward the
/// gateway) and RateOutBps its downloads, on every writer path - and a mesh child absorbed via a
/// parent's downlink_table must not have its aggregate overwritten from the stale "parent" port
/// its own uplink block names (that port is on the wrong link AND reads inverted).
/// </summary>
public class LanFabricAggregatorTests
{
    private const string SwitchMac = "aa:bb:cc:00:00:02";
    private const string KitchenMac = "aa:bb:cc:00:00:05";   // true mesh parent
    private const string FrontYardMac = "aa:bb:cc:00:00:06"; // absorbed mesh child
    private const string GarageMac = "aa:bb:cc:00:00:07";    // switch really hanging off the child

    private static MonitoringLiveStats NewLiveStats() =>
        new(NullLogger<MonitoringLiveStats>.Instance,
            new Mock<IDbContextFactory<NetworkOptimizerDbContext>>().Object);

    private static UniFiDeviceResponse Ap(string mac, string? uplinkMac, string uplinkType = "wire",
        int uplinkRemotePort = 0) => new()
    {
        Mac = mac,
        Type = "uap",
        Model = "U6M",
        Uplink = uplinkMac == null ? null : new UplinkInfo
        {
            UplinkMac = uplinkMac,
            Type = uplinkType,
            UplinkRemotePort = uplinkRemotePort,
        },
    };

    private static UniFiDeviceResponse Switch(string mac, string? uplinkMac, int uplinkRemotePort = 0) => new()
    {
        Mac = mac,
        Type = "usw",
        Model = "USL8LP",
        Uplink = uplinkMac == null ? null : new UplinkInfo
        {
            UplinkMac = uplinkMac,
            Type = "wire",
            UplinkRemotePort = uplinkRemotePort,
        },
    };

    private static void Claim(UniFiDeviceResponse parent, string childMac, long txKbps = 650_000, long rxKbps = 433_000)
        => parent.DownlinkTable = new List<DownlinkTableEntry>
        {
            new() { Mac = "vwire-bssid", SerialNo = childMac, TxRate = txKbps, RxRate = rxKbps },
        };

    [Fact]
    public void WiredAp_AggregateComesFromParentPort_RateInIsUploads()
    {
        // Parent port perspective: port RX = bytes from the child = the child's uploads.
        var fabric = new LanFabricAggregator();
        var liveStats = NewLiveStats();
        fabric.SetSnmpPortRate(SwitchMac, 4, rateInBps: 111, rateOutBps: 222);
        var devices = new List<UniFiDeviceResponse>
        {
            Switch(SwitchMac, uplinkMac: null),
            Ap(FrontYardMac, uplinkMac: SwitchMac, uplinkRemotePort: 4),
        };

        fabric.WriteAggregates(devices, liveStats, DateTime.UtcNow);

        var stats = liveStats.GetForDevice(FrontYardMac)!;
        stats.RateInBps.Should().Be(111, "parent port RX = child's uploads = RateInBps");
        stats.RateOutBps.Should().Be(222, "parent port TX = downloads to the child = RateOutBps");
    }

    [Fact]
    public void AbsorbedChild_StaleParentPortIsNotRead_VwirestaAggregateStands()
    {
        // Front Yard's stale uplink names Garage, whose port has SNMP data (the wire is real,
        // the hierarchy is inverted). Reading it overwrote the correct vwiresta aggregate with
        // wrong-link, inverted numbers on every cycle - the live map's 80/20 direction flip.
        var fabric = new LanFabricAggregator();
        var liveStats = NewLiveStats();
        fabric.SetSnmpPortRate(GarageMac, 8, rateInBps: 999, rateOutBps: 888);
        var kitchen = Ap(KitchenMac, uplinkMac: null);
        Claim(kitchen, FrontYardMac);
        var devices = new List<UniFiDeviceResponse>
        {
            kitchen,
            Ap(FrontYardMac, uplinkMac: GarageMac, uplinkRemotePort: 8),
            Switch(GarageMac, uplinkMac: FrontYardMac, uplinkRemotePort: 1),
        };
        // The fast tier's vwiresta write (uploads into RateInBps) precedes WriteAggregates.
        liveStats.RecordInterfaceAggregate(FrontYardMac, 50, 75, DateTime.UtcNow);

        fabric.WriteAggregates(devices, liveStats, DateTime.UtcNow);

        var stats = liveStats.GetForDevice(FrontYardMac)!;
        stats.RateInBps.Should().Be(50, "the vwiresta aggregate must stand");
        stats.RateOutBps.Should().Be(75);
    }

    [Fact]
    public void AbsorbedChild_WithoutSnmp_SynthesizesFromItsRealChildren()
    {
        // No vwiresta aggregate landed; the mesh synthesis pass must cover the absorbed child
        // even though its stale uplink block still says "wire".
        var fabric = new LanFabricAggregator();
        var liveStats = NewLiveStats();
        var kitchen = Ap(KitchenMac, uplinkMac: null);
        Claim(kitchen, FrontYardMac);
        var devices = new List<UniFiDeviceResponse>
        {
            kitchen,
            Ap(FrontYardMac, uplinkMac: GarageMac, uplinkRemotePort: 8),
            Switch(GarageMac, uplinkMac: FrontYardMac, uplinkRemotePort: 1),
        };
        // Garage's own boundary aggregate (uploads=10, downloads=20), as the first pass would
        // have left it; Front Yard's synthesis sums its children on the same sides.
        liveStats.RecordInterfaceAggregate(GarageMac, 10, 20, DateTime.UtcNow);

        fabric.WriteAggregates(devices, liveStats, DateTime.UtcNow);

        var stats = liveStats.GetForDevice(FrontYardMac)!;
        stats.RateInBps.Should().Be(10, "child uploads sum into RateInBps");
        stats.RateOutBps.Should().Be(20, "child downloads sum into RateOutBps");
    }

    [Fact]
    public void AgreeingMeshChild_IsNotTreatedAsAbsorbed()
    {
        // A healthy mesh child (self-reported wireless uplink to the same parent that claims it)
        // must go through the same passes as before the derived-parent work: nothing skipped.
        var fabric = new LanFabricAggregator();
        var liveStats = NewLiveStats();
        var kitchen = Ap(KitchenMac, uplinkMac: null);
        Claim(kitchen, FrontYardMac);
        var devices = new List<UniFiDeviceResponse>
        {
            kitchen,
            Ap(FrontYardMac, uplinkMac: KitchenMac, uplinkType: "wireless"),
        };
        liveStats.RecordInterfaceAggregate(FrontYardMac, 50, 75, DateTime.UtcNow);

        fabric.WriteAggregates(devices, liveStats, DateTime.UtcNow);

        var stats = liveStats.GetForDevice(FrontYardMac)!;
        stats.RateInBps.Should().Be(50, "the SNMP (vwiresta) aggregate wins for a healthy mesh child too");
        stats.RateOutBps.Should().Be(75);
    }

    // ---- Building Bridge pairs ----
    // One unit is listed and wired to a switch; the other is nested in its peer_ubb, uplinked
    // wirelessly to the listed one, and is the unit the far building's switch hangs off.

    private const string BridgeWiredMac = "aa:bb:cc:00:00:10";
    private const string BridgeWirelessMac = "aa:bb:cc:00:00:20";

    private static UniFiDeviceResponse BridgePair(long peerTxBytes, long peerRxBytes) => new()
    {
        Mac = BridgeWiredMac,
        Type = "ubb",
        Model = "UBB",
        Uplink = new UplinkInfo { UplinkMac = SwitchMac, Type = "wire", UplinkRemotePort = 17 },
        PeerUbb = new UniFiDeviceResponse
        {
            Mac = BridgeWirelessMac,
            Type = "ubb",
            Model = "UBB",
            Uplink = new UplinkInfo
            {
                UplinkMac = BridgeWiredMac,
                Type = "wireless",
                TxBytes = peerTxBytes,
                RxBytes = peerRxBytes,
            },
        },
    };

    [Fact]
    public void WiredBridgeUnit_AggregateComesFromParentPort()
    {
        var fabric = new LanFabricAggregator();
        var liveStats = NewLiveStats();
        fabric.SetSnmpPortRate(SwitchMac, 17, rateInBps: 111, rateOutBps: 222);
        var devices = new List<UniFiDeviceResponse> { Switch(SwitchMac, uplinkMac: null), BridgePair(0, 0) };

        fabric.WriteAggregates(devices, liveStats, DateTime.UtcNow);

        var stats = liveStats.GetForDevice(BridgeWiredMac)!;
        stats.RateInBps.Should().Be(111, "parent port RX = the unit's uploads = RateInBps");
        stats.RateOutBps.Should().Be(222);
    }

    [Fact]
    public void WirelessBridgeUnit_AggregateComesFromItsUplinkCounters_RateInIsUploads()
    {
        // The nested unit is on no device list; its uplink counters are the only account of the
        // span. Unit perspective: tx = toward the peer = uploads, rx = from it = downloads.
        var fabric = new LanFabricAggregator();
        var liveStats = NewLiveStats();
        var t0 = DateTime.UtcNow;
        var sw = Switch(SwitchMac, uplinkMac: null);

        fabric.UpdateUnifiPortRates([sw, BridgePair(peerTxBytes: 1_000, peerRxBytes: 5_000)], t0);
        fabric.UpdateUnifiPortRates([sw, BridgePair(peerTxBytes: 2_000, peerRxBytes: 9_000)], t0.AddSeconds(10));
        fabric.WriteAggregates([sw, BridgePair(peerTxBytes: 2_000, peerRxBytes: 9_000)], liveStats, t0.AddSeconds(10));

        var stats = liveStats.GetForDevice(BridgeWirelessMac)!;
        stats.RateInBps.Should().Be(800, "1000 bytes sent in 10 s = 800 bps of uploads into RateInBps");
        stats.RateOutBps.Should().Be(3200, "4000 bytes received in 10 s = 3200 bps of downloads into RateOutBps");
    }

    [Fact]
    public void WirelessBridgeUnit_UnchangedCountersKeepTheLastRate()
    {
        // The console refreshes the block ~30 s; a repeated sample must not read as a zero rate.
        var fabric = new LanFabricAggregator();
        var t0 = DateTime.UtcNow;
        var sw = Switch(SwitchMac, uplinkMac: null);

        fabric.UpdateUnifiPortRates([sw, BridgePair(1_000, 5_000)], t0);
        fabric.UpdateUnifiPortRates([sw, BridgePair(2_000, 9_000)], t0.AddSeconds(10));
        fabric.UpdateUnifiPortRates([sw, BridgePair(2_000, 9_000)], t0.AddSeconds(15));

        fabric.UplinkRate(BridgeWirelessMac).Should().Be((3200d, 800d));
    }

    [Fact]
    public void WirelessBridgeUnit_HasNoAggregateBeforeASecondSample()
    {
        var fabric = new LanFabricAggregator();
        var liveStats = NewLiveStats();
        var devices = new List<UniFiDeviceResponse> { Switch(SwitchMac, uplinkMac: null), BridgePair(1_000, 5_000) };

        fabric.UpdateUnifiPortRates(devices, DateTime.UtcNow);
        fabric.WriteAggregates(devices, liveStats, DateTime.UtcNow);

        liveStats.GetForDevice(BridgeWirelessMac).Should().BeNull();
    }
}
