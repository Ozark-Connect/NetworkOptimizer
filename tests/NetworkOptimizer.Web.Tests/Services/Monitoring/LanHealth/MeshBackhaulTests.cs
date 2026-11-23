using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.Monitoring.LanHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services.Monitoring.LanHealth;

/// <summary>
/// Mesh backhaul quality: the parent's downlink_table side of the link, probe loss and RTT against the parent,
/// re-associations, channel moves, and demand. Values mirror the 2026-11 captures of a U7 Pro to UDB link and
/// an MLO U7 Outdoor pair.
/// </summary>
public class MeshBackhaulTests
{
    private const string ParentMac = "aa:bb:cc:00:00:10";
    private const string ChildMac = "aa:bb:cc:00:00:20";
    private static readonly DateTime Hour = new(2026, 11, 23, 18, 0, 0, DateTimeKind.Utc);

    private static LinkRecord Mesh(string radio = "na", long phyKbps = 866_000) => new()
    {
        Id = $"mesh:{ParentMac}-{ChildMac}",
        Kind = LinkKind.FabricMesh,
        Upstream = new LinkEnd { DeviceMac = ParentMac, DeviceName = "AP Parent", DeviceType = DeviceType.AccessPoint },
        Downstream = new LinkEnd { DeviceMac = ChildMac, DeviceName = "Bridge Child", DeviceType = DeviceType.DeviceBridge },
        MeshRadio = radio,
        MeshTxRateKbps = phyKbps,
        ClientsBehind = 2,
    };

    private static LinkRecord WiredToParent() => new()
    {
        Id = "wired-parent",
        Kind = LinkKind.FabricWired,
        Upstream = new LinkEnd { DeviceMac = "aa:bb:cc:00:00:01", DeviceName = "Switch", DeviceType = DeviceType.Switch, PortIdx = 3 },
        Downstream = new LinkEnd { DeviceMac = ParentMac, DeviceName = "AP Parent", DeviceType = DeviceType.AccessPoint },
        SpeedMbps = 2500,
    };

    private static UniFiDeviceResponse Child(string mac = ChildMac) => new()
    {
        Mac = mac,
        Uplink = new UplinkInfo { Type = "wireless", UplinkMac = ParentMac },
    };

    [Fact]
    public void MeshPeers_FollowTheChildsUplinkToItsParentsEntries()
    {
        var parent = new UniFiDeviceResponse
        {
            Mac = ParentMac,
            DownlinkTable =
            [
                new DownlinkTableEntry { Mac = "96:41:b2:16:50:13", SerialNo = "AA:BB:CC:00:00:20", Name = "vwireap14", IsMlo = true, Radio = "6e", Signal = -58 },
                new DownlinkTableEntry { Mac = "9a:41:b2:16:50:12", SerialNo = "aa:bb:cc:00:00:20", Name = "vwireap10", IsMlo = true, Radio = "na", Signal = -45 },
                new DownlinkTableEntry { Mac = "aa:bb:cc:00:00:99" },
                new DownlinkTableEntry { Mac = "y", MldMac = "aa:bb:cc:00:00:31", Radio = "na" },
                new DownlinkTableEntry { Mac = "z", MldMac = "aa:bb:cc:00:00:31", Radio = "na" },
                new DownlinkTableEntry { Mac = "x", SerialNo = "aa:bb:cc:00:00:30", Active = false },
            ],
        };
        var peers = LanPortSnapshotRecorder.MeshPeers([parent, Child(), Child("aa:bb:cc:00:00:31"), Child("aa:bb:cc:00:00:30")]);
        peers.Keys.Should().BeEquivalentTo([ChildMac, "aa:bb:cc:00:00:31"], "an inactive link (a Building Bridge's idle fallback) is no peer");
        peers[ChildMac].Should().HaveCount(2, "an MLO backhaul is one entry per link");
        peers["aa:bb:cc:00:00:31"].Should().ContainSingle("a non-MLO child keeps its first entry, matched by mld_mac when serialno is absent");
    }

    [Fact]
    public void WeakBelowDbm_AgreesWithIsWeakSignal()
    {
        foreach (var band in new[] { NetworkOptimizer.WiFi.Models.RadioBand.Band2_4GHz, NetworkOptimizer.WiFi.Models.RadioBand.Band5GHz, NetworkOptimizer.WiFi.Models.RadioBand.Band6GHz })
        {
            var below = NetworkOptimizer.WiFi.Helpers.SignalClassification.WeakBelowDbm(band);
            NetworkOptimizer.WiFi.Helpers.SignalClassification.IsWeakSignal(below, band).Should().BeFalse();
            NetworkOptimizer.WiFi.Helpers.SignalClassification.IsWeakSignal(below - 1, band).Should().BeTrue();
        }
    }

    [Fact]
    public void WorstSignal_GradesEachEndOnItsOwnBand()
    {
        // 6 GHz at -80 is 7 dB clear of weak; 5 GHz at -70 is 8 dB clear: the 6 GHz reading is closer to weak.
        var w = LanHealthEvidenceBuilder.WorstSignal([new(-70, "na", false), new(-80, "6e", true)])!;
        w.Radio.Should().Be("6e");
        w.AtParent.Should().BeTrue();
    }

    [Fact]
    public void WeakMeshSignal_NamesTheWeakEnd_AndAsymmetryAloneIsNotAFault()
    {
        // The child hears its parent at -40 / -33; the parent hears it at -58 / -45: all good for their bands.
        Classes(new LinkEvidence { Link = Mesh("6e"), ParentName = "AP Parent", MeshWorstSignalDbm = -58, MeshWorstSignalRadio = "6e", MeshWorstSignalAtParent = true }).Should().BeEmpty();
        var f = LanHealthDetectors.MeshQuality(new LinkEvidence { Link = Mesh(), ParentName = "AP Parent", MeshWorstSignalDbm = -80, MeshWorstSignalRadio = "na", MeshWorstSignalAtParent = true }, -55).Single();
        f.Body.Should().Be("AP Parent hears this device at -80 dBm on 5 GHz, weak for the band.");
    }

    [Fact]
    public void MeshRetries_ReportTheWorseDirection()
    {
        var f = LanHealthDetectors.MeshQuality(new LinkEvidence { Link = Mesh(), ParentName = "AP Parent", MeshRetryPct = 6, MeshChildRetryPct = 22, MeshChildRetryPctBaseline = 7 }, null).Single();
        f.Body.Should().StartWith("This device retries 22% of its transmissions to AP Parent, against its normal 7%.");
    }

    [Fact]
    public void DeviceFields_SumTheParentsMloEntriesOntoTheChild()
    {
        var child = new UniFiDeviceResponse
        {
            Mac = ChildMac,
            Uplink = new UplinkInfo { Type = "wireless", Signal = -40, TxRate = 4_323_600, UplinkMac = ParentMac },
        };
        var side = new List<DownlinkTableEntry>
        {
            new() { TxRetries = 464_922, WifiTxAttempts = 6_818_682, WifiTxDropped = 0, Uptime = 43_826, TxBytesRate = 15_000, RxBytesRate = 1_000_000 },
            new() { TxRetries = 755_401, WifiTxAttempts = 9_966_444, WifiTxDropped = 3, Uptime = 43_820, TxBytesRate = 5_000, RxBytesRate = 250_000 },
        };
        var f = LanPortSnapshotRecorder.DeviceFields(child, side).ToDictionary(kv => kv.Key, kv => kv.Value);
        f[InfluxFieldNames.UplinkTxRetries].Should().Be(1_220_323L);
        f[InfluxFieldNames.UplinkTxAttempts].Should().Be(16_785_126L);
        f[InfluxFieldNames.UplinkTxDropped].Should().Be(3L);
        f[InfluxFieldNames.UplinkAssocSeconds].Should().Be(43_820L, "the shortest link's uptime is the latest re-association");
        f[InfluxFieldNames.UplinkRxBps].Should().Be(10_000_000.0);
        f[InfluxFieldNames.UplinkTxBps].Should().Be(160_000.0);
    }

    [Theory]
    [InlineData(100.0, 400.0, 300.0)]
    [InlineData(5_000.0, 200.0, 200.0)]   // reset: the counters restarted at re-association
    public void CounterRise_IsLastMinusFirstOrTheLastAfterAReset(double first, double last, double rise) =>
        LanHealthEvidenceBuilder.CounterRise(first, last).Should().Be(rise);

    [Fact]
    public void Reassociation_FromTheParentsAssociationUptime()
    {
        var to = Hour.AddHours(1);
        // Uptime fell inside the hour: re-associated 600 s before the last reading.
        LanHealthEvidenceBuilder.Reassociation(Hour, to, 25_000, 600).Should().Be(to.AddSeconds(-600));
        // Associated 20 minutes in, first reading already after it.
        LanHealthEvidenceBuilder.Reassociation(Hour, to, 60, 2_400).Should().Be(to.AddSeconds(-2_400));
        // Steady association: none.
        LanHealthEvidenceBuilder.Reassociation(Hour, to, 40_000, 43_500).Should().BeNull();
        LanHealthEvidenceBuilder.Reassociation(Hour, to, null, null).Should().BeNull();
    }

    [Fact]
    public void MedianGap_ComparesEachHourWithTheParentsSameHour()
    {
        var rows = Enumerable.Range(0, 3).Select(i => new LanHealthLinkHour { HourStart = Hour.AddHours(i), ProbeRttMs = 2.2 + i }).ToList();
        var parent = Enumerable.Range(0, 3).Select(i => new LanHealthLinkHour { HourStart = Hour.AddHours(i), ProbeRttMs = 0.3 }).ToList();
        LanHealthReportBuilder.MedianGap(rows, parent, 1).Should().BeApproximately(2.9, 1e-9);
        LanHealthReportBuilder.MedianGap(rows, parent, 24).Should().BeNull("a normal needs a day of hours");
    }

    [Fact]
    public void RetryPct_NeedsEnoughAttempts()
    {
        LanHealthReportBuilder.RetryPct([new LanHealthLinkHour { MeshTxRetries = 54_000, MeshTxAttempts = 1_000_000 }]).Pct.Should().BeApproximately(5.4, 1e-9);
        LanHealthReportBuilder.RetryPct([new LanHealthLinkHour { MeshTxRetries = 500, MeshTxAttempts = 1_000 }]).Pct.Should().BeNull();
    }

    private static LinkEvidence Ev(LinkRecord link, double? loss = null, double? parentLoss = null, double? gap = null, double? gapNormal = null,
        double? retry = null, double? retryNormal = null, double dropped = 0, IEnumerable<LinkHourSample>? hours = null) => new()
    {
        Link = link,
        ProbeLossPct = loss,
        ParentProbeLossPct = parentLoss,
        ParentName = "AP Parent",
        RttGapMs = gap,
        RttGapBaselineMs = gapNormal,
        MeshRetryPct = retry,
        MeshRetryPctBaseline = retryNormal,
        MeshTxDropped = dropped,
        Hours = hours?.ToList() ?? [],
    };

    private static List<string> Classes(LinkEvidence ev, double? signal = null) =>
        LanHealthDetectors.MeshQuality(ev, signal).Select(f => f.Class).ToList();

    [Fact]
    public void HealthyBackhaul_HasNoFindings() =>
        Classes(Ev(Mesh(), loss: 0, parentLoss: 0, gap: 2.1, gapNormal: 1.9, retry: 6, retryNormal: 5.5), signal: -57).Should().BeEmpty();

    [Theory]
    [InlineData("na", -80, "weak")]
    [InlineData("na", -88, "poor")]
    [InlineData("6e", -80, null)]
    public void WeakMeshSignal_UsesTheBandsOwnLevels(string radio, int dbm, string? level)
    {
        var f = LanHealthDetectors.MeshQuality(Ev(Mesh(radio)), dbm).SingleOrDefault(x => x.Class == "Weak Mesh Signal");
        if (level == null) f.Should().BeNull();
        else f!.Body.Should().Contain(level);
    }

    [Fact]
    public void MeshBackhaulLoss_CountsOnlyLossBeyondTheParents()
    {
        Classes(Ev(Mesh(), loss: 0.8, parentLoss: 0.4)).Should().BeEmpty("loss the wired parent shares is upstream");
        Classes(Ev(Mesh(), loss: 1.2, parentLoss: 0.3)).Should().Equal("Mesh Backhaul Loss");
    }

    [Fact]
    public void MeshBackhaulLatency_IsARiseOverItsOwnNormalGap()
    {
        Classes(Ev(Mesh(), gap: 3.5, gapNormal: 1.9)).Should().BeEmpty();
        Classes(Ev(Mesh(), gap: 4.0, gapNormal: 1.9)).Should().Equal("Mesh Backhaul Latency");
    }

    [Fact]
    public void MeshRetries_NeedTheFloorAndTwiceItsNormal()
    {
        Classes(Ev(Mesh(), retry: 16, retryNormal: 9)).Should().BeEmpty();
        var f = LanHealthDetectors.MeshQuality(Ev(Mesh(), retry: 18, retryNormal: 6, dropped: 1200), null).Single();
        f.Class.Should().Be("Mesh Retries");
        f.Body.Should().Contain("18%").And.Contain("6%").And.Contain("1,200");
    }

    [Fact]
    public void MeshBackhaulSaturated_NeedsThreeHoursPastHalfItsUsableRate()
    {
        // 866 Mbps PHY carries about 476 Mbps; half is about 238 Mbps.
        LinkHourSample H(int i, double bps) => new(Hour.AddHours(i), null, null, bps * 0.9, bps * 0.1, null, null, null, null, null);
        Classes(Ev(Mesh(), hours: [H(0, 300e6), H(1, 300e6), H(2, 100e6)])).Should().BeEmpty();
        Classes(Ev(Mesh(), hours: [H(0, 300e6), H(1, 300e6), H(2, 250e6)])).Should().Equal("Mesh Backhaul Saturated");
    }

    [Fact]
    public void ChannelMoveWindows_CoverTheMeshChildrenOnly()
    {
        var moves = new[] { new ApChannelChange { ApMac = "AA:BB:CC:00:00:10", PreviousChannel = 116, NewChannel = 36, ChangedAtUtc = Hour } };
        var w = LanHealthEngine.BackhaulChannelWindows(moves, [Mesh(), WiredToParent()]).ToList();
        w.Should().ContainSingle().Which.DeviceMac.Should().Be(ChildMac);
        w[0].Detail.Should().Be("116>36");
        LanHealthDetectors.ChannelMoveText("116>36").Should().Be("Channel change (116 to 36, off DFS)");
        LanHealthDetectors.ChannelMoveText("36>149").Should().Be("Channel change (36 to 149)");
    }

    [Fact]
    public void BackhaulChannelChanges_AfterThreeMovesInTheWindow()
    {
        LinkIncident Move(int i, string d) => new() { Start = Hour.AddHours(i), End = Hour.AddHours(i), Kind = IncidentKind.Drop, Explained = ExplainReason.BackhaulChannelChange, ExplainedDetail = d };
        var ev = Ev(Mesh());
        LanHealthDetectors.BackhaulChannelMoves(ev, [Move(0, "116>36"), Move(1, "36>40")], FindingText.Utc).Should().BeNull();
        var f = LanHealthDetectors.BackhaulChannelMoves(ev, [Move(0, "116>36"), Move(1, "36>40"), Move(2, "100>40")], FindingText.Utc)!;
        f.Body.Should().Contain("3 times").And.Contain("2 of them left a DFS channel");
    }
}
