using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Web.Services.Monitoring.LanHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services.Monitoring.LanHealth;

/// <summary>
/// The per-link detectors not covered by the replay and calibration tests: each fires on its signature and
/// stays quiet on its near miss.
/// </summary>
public class LinkDetectorTests
{
    private static readonly DateTime T0 = new(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T1 = T0.AddHours(48);

    private static LinkRecord Fabric(int speed = 2500, bool forced = false, int? cap = null, bool? fullDuplex = true,
        DeviceType down = DeviceType.AccessPoint, PortConfig? port = null) => new()
    {
        Id = "sw|5-ap|1",
        Kind = LinkKind.FabricWired,
        Upstream = new LinkEnd { DeviceMac = "aa:bb:cc:00:00:02", DeviceName = "Switch A", DeviceType = DeviceType.Switch, PortIdx = 5 },
        Downstream = new LinkEnd { DeviceMac = "aa:bb:cc:00:00:03", DeviceName = "AP 1", DeviceType = down, PortIdx = 1 },
        SpeedMbps = speed,
        Forced = forced,
        CapabilityMbps = cap,
        FullDuplex = fullDuplex,
        UpstreamPort = port ?? new PortConfig(),
        ClientsBehind = 6,
    };

    private static LinkRecord Access(int speed = 1000, bool? fullDuplex = true, int macs = 1) => new()
    {
        Id = "sw|7-client",
        Kind = LinkKind.Access,
        Upstream = new LinkEnd { DeviceMac = "aa:bb:cc:00:00:02", DeviceName = "Switch A", DeviceType = DeviceType.Switch, PortIdx = 7 },
        Downstream = new LinkEnd { DeviceMac = "aa:bb:cc:00:00:99", DeviceName = "PC 1" },
        SpeedMbps = speed,
        FullDuplex = fullDuplex,
        UpstreamPort = new PortConfig { MacTableCount = macs },
        ClientsBehind = 1,
    };

    private static LinkEvidence Ev(LinkRecord link, Action<LinkEvidenceBuilderShim>? set = null)
    {
        var s = new LinkEvidenceBuilderShim();
        set?.Invoke(s);
        return new LinkEvidence
        {
            Link = link, From = T0, To = T1,
            Upstream = s.Up, Downstream = s.Down, Hours = s.Hours, BaselineHours = s.Baseline, Speeds = s.Speeds,
            UsualSpeedMbps = s.Usual, Mesh = s.Mesh, MeshBaseline = s.MeshBaseline,
            PoeDraw = s.Poe, ProbeLossPct = s.Loss, ParentProbeLossPct = s.ParentLoss,
            SfpRxPowerDbm = s.Rx, SfpRxPowerBestDbm = s.RxBest, SfpTemperatureC = s.Temp, SpeedTests = s.Tests,
        };
    }

    /// <summary>Mutable bag for the evidence a test sets; LinkEvidence itself is init-only.</summary>
    public sealed class LinkEvidenceBuilderShim
    {
        public EndCounters Up { get; } = new();
        public EndCounters Down { get; } = new();
        public List<LinkHourSample> Hours { get; } = [];
        public List<LinkHourSample> Baseline { get; } = [];
        public List<SpeedReading> Speeds { get; } = [];
        public int? Usual { get; set; }
        public List<MeshReading> Mesh { get; } = [];
        public List<MeshReading> MeshBaseline { get; } = [];
        public List<DateTime> Reboots { get; } = [];
        public List<(DateTime, double)> Poe { get; } = [];
        public double? Loss { get; set; }
        public double? ParentLoss { get; set; }
        public double? Rx { get; set; }
        public double? RxBest { get; set; }
        public double? Temp { get; set; }
        public List<LinkSpeedTest> Tests { get; } = [];
    }

    private static LinkIncident Incident(IncidentKind kind, DateTime at, double count = 1) =>
        new() { Start = at, End = at.AddSeconds(3), Kind = kind, Count = count };

    private static LinkHourSample Hour(int i, double? meanIn = null, double? bcast = null, double? discards = null, double? txPackets = null) =>
        new(T0.AddHours(i), meanIn, null, meanIn, null, bcast, null, null, discards, null, txPackets);

    // ---- Speed downshift ----

    [Fact]
    public void SpeedDownshift_FabricBelowItsUsualSpeed()
    {
        var f = LanHealthDetectors.SpeedDownshift(Ev(Fabric(1000), s => { s.Usual = 2500; s.Speeds.Add(new(T0.AddHours(3), 2500, true, LinkSource.PortTableState)); }), FindingText.Utc)!;
        f.Class.Should().Be("Link Speed Degraded");
        f.Body.Should().StartWith("Linked at 1 Gbps since");
        f.Chips.Should().Contain("Speed downshift");
    }

    [Fact]
    public void SpeedDownshift_AlternatingSpeedsAreNamed()
    {
        var f = LanHealthDetectors.SpeedDownshift(Ev(Fabric(1000), s =>
        {
            s.Usual = 2500;
            s.Speeds.AddRange([new(T0, 2500, true, LinkSource.PortTableState), new(T0.AddHours(1), 1000, true, LinkSource.PortTableState), new(T0.AddHours(2), 100, true, LinkSource.PortTableState)]);
        }), FindingText.Utc)!;
        f.Chips.Should().Contain("Alternating speed");
    }

    [Fact]
    public void SpeedDownshift_NotOnAForcedPortOrAnAccessPortOrAtSpeed()
    {
        LanHealthDetectors.SpeedDownshift(Ev(Fabric(1000, forced: true), s => s.Usual = 2500), FindingText.Utc).Should().BeNull();
        LanHealthDetectors.SpeedDownshift(Ev(Access(100), s => s.Usual = 1000), FindingText.Utc).Should().BeNull("access ports are AccessDegraded's");
        LanHealthDetectors.SpeedDownshift(Ev(Fabric(2500), s => s.Usual = 2500), FindingText.Utc).Should().BeNull();
    }

    // ---- Duplex ----

    [Fact]
    public void DuplexMismatch_HalfDuplexOrLateCollisions()
    {
        LanHealthDetectors.DuplexMismatch(Ev(Fabric(fullDuplex: false)))!.Body.Should().StartWith("Half duplex.");
        LanHealthDetectors.DuplexMismatch(Ev(Fabric(), s => s.Up.LateCollisions = 12))!.Body.Should().StartWith("12 late collisions: the far end");
        LanHealthDetectors.DuplexMismatch(Ev(Fabric())).Should().BeNull();
    }

    [Fact]
    public void DuplexMismatch_LeavesAHubOnAnAccessPortToHiddenSwitch() =>
        LanHealthDetectors.DuplexMismatch(Ev(Access(fullDuplex: false, macs: 5))).Should().BeNull();

    // ---- Errors and which end ----

    [Fact]
    public void StableLinkErrors_AboveTheFloor_NamesTheReceivingEnd()
    {
        var incidents = new[] { Incident(IncidentKind.ErrorBurst, T0.AddHours(1), 40) };
        var f = LanHealthDetectors.StableLinkErrors(Ev(Fabric(), s => { s.Up.RxErrors = 0; s.Down.RxErrors = 40; s.Down.RxPackets = 4_000_000; }), incidents, FindingText.Utc)!;
        f.Detector.Should().Be(Detectors.ErrorAsymmetry);
        f.Body.Should().Contain("40 receive errors").And.Contain("(10 per million packets)").And.Contain(", all at AP 1.");
    }

    [Fact]
    public void StableLinkErrors_BothEnds_IsTheSymmetricFinding()
    {
        var incidents = new[] { Incident(IncidentKind.ErrorBurst, T0.AddHours(1), 40) };
        LanHealthDetectors.StableLinkErrors(Ev(Fabric(), s => { s.Up.RxErrors = 20; s.Down.RxErrors = 20; }), incidents, FindingText.Utc)!
            .Detector.Should().Be(Detectors.StableLinkErrors);
    }

    [Fact]
    public void StableLinkErrors_AtOrUnderFivePerDay_IsNothing() =>
        LanHealthDetectors.StableLinkErrors(Ev(Fabric()), [Incident(IncidentKind.ErrorBurst, T0, 10)], FindingText.Utc).Should().BeNull("10 over two days is 5 a day");

    [Fact]
    public void ErrorAsymmetry_TrustsABlindGatewayEnd()
    {
        LanHealthDetectors.ErrorAsymmetry(Ev(Fabric(), s => { s.Up.ErrorsBlind = true; s.Down.RxErrors = 9; }))!.Value.End.Should().Be("AP 1");
        LanHealthDetectors.ErrorAsymmetry(Ev(Fabric(), s => s.Down.RxErrors = 9)).Should().BeNull("an end that reports nothing is not an end with none");
    }

    // ---- Loss with no counter evidence ----

    [Fact]
    public void LossWithoutEvidence_ThreeBurstsTheParentDoesNotShare()
    {
        var bursts = Enumerable.Range(0, 3).Select(i => Incident(IncidentKind.LossBurst, T0.AddHours(i))).ToList();
        LanHealthDetectors.LossWithoutEvidence(Ev(Fabric(), s => { s.Loss = 0.4; s.ParentLoss = 0.05; }), bursts, FindingText.Utc)!.Class.Should().Be("Packet Loss");
        LanHealthDetectors.LossWithoutEvidence(Ev(Fabric(), s => { s.Loss = 0.4; s.ParentLoss = 0.3; }), bursts, FindingText.Utc).Should().BeNull("upstream's loss");
        LanHealthDetectors.LossWithoutEvidence(Ev(Fabric(down: DeviceType.Switch)), bursts, FindingText.Utc).Should().BeNull("a switch answers ICMP from its control plane");
        LanHealthDetectors.LossWithoutEvidence(Ev(Fabric()), bursts.Take(2).ToList(), FindingText.Utc).Should().BeNull();
    }

    // ---- Microbursts ----

    [Fact]
    public void Microbursts_UnusualDiscardsOnAQuietLink()
    {
        // A switch port's normal is 10 per million; 500 discards over 1M packets is 500.
        var f = LanHealthDetectors.Microbursts(Ev(Fabric(1000), s => { s.Up.TxDiscards = 500; s.Up.TxPackets = 1_000_000; s.Hours.Add(Hour(0, meanIn: 50e6)); }))!;
        f.Body.Should().StartWith("500 packets discarded while the link averaged under 5% of its speed:");
        LanHealthDetectors.Microbursts(Ev(Fabric(1000), s => { s.Up.TxDiscards = 500; s.Up.TxPackets = 1_000_000; s.Hours.Add(Hour(0, meanIn: 600e6)); }))
            .Should().BeNull("a busy link's discards are load");
    }

    // ---- Broadcast storm ----

    [Fact]
    public void Storm_TenTimesItsNormalAndOverTheFloor()
    {
        var f = LanHealthDetectors.Storm(Ev(Fabric(port: new PortConfig { StormControlBroadcast = false }), s =>
        {
            s.Baseline.AddRange(Enumerable.Range(-48, 48).Select(i => Hour(i, bcast: 5)));
            s.Hours.AddRange([Hour(0, bcast: 6), Hour(1, bcast: 400)]);
        }), FindingText.Utc)!;
        f.Body.Should().StartWith("Broadcast hit 400 packets per second, against a normal of 5").And.EndWith("Storm control is off for this port.");
        LanHealthDetectors.Storm(Ev(Fabric(), s => s.Hours.Add(Hour(0, bcast: 40))), FindingText.Utc).Should().BeNull("under 50 pps");
    }

    // ---- Optics ----

    [Fact]
    public void Optics_DriftHeatOrLowPower()
    {
        LanHealthDetectors.Optics(Ev(Fabric(), s => { s.Rx = -9; s.RxBest = -5; }))!.Body.Should().StartWith("SFP receive power is 4 dB below its best this week (-9 dBm).");
        LanHealthDetectors.Optics(Ev(Fabric(), s => s.Rx = -26))!.Body.Should().StartWith("SFP receive power is -26 dBm.");
        LanHealthDetectors.Optics(Ev(Fabric(), s => s.Temp = 80))!.Body.Should().Contain("The module runs at 80 °C.");
        LanHealthDetectors.Optics(Ev(Fabric(), s => { s.Rx = -6; s.RxBest = -5; s.Temp = 50; })).Should().BeNull();
    }

    // ---- Below capability ----

    [Fact]
    public void BelowCapability_BothEndsCanDoMore()
    {
        LanHealthDetectors.BelowCapability(Ev(Fabric(1000, cap: 2500)))!.Body.Should().StartWith("Both ends support 2.5 Gbps, but the link runs at 1 Gbps.");
        LanHealthDetectors.BelowCapability(Ev(Fabric(1000, cap: 2500, forced: true))).Should().BeNull();
        LanHealthDetectors.BelowCapability(Ev(Fabric(2500, cap: 2500))).Should().BeNull();
    }

    // ---- PoE ----

    [Fact]
    public void PoeCycling_CountsOnlyPowerLossReboots()
    {
        LinkIncident Reboot(int h, string cause) => new() { Start = T0.AddHours(h), End = T0.AddHours(h).AddSeconds(40), Kind = IncidentKind.Drop, Explained = ExplainReason.DeviceRebooted, ExplainedDetail = cause };
        var powered = new PortConfig { PoeMode = "auto", PoePowerW = 9 };
        var f = LanHealthDetectors.PoeCycling(Ev(Fabric(port: powered)), [Reboot(1, "PowerLoss"), Reboot(9, "PowerLoss")])!;
        f.Body.Should().Be("AP 1 lost power 2 times. Check Switch A's PoE budget and the cable.");
        LanHealthDetectors.PoeCycling(Ev(Fabric(port: powered)), [Reboot(1, "Watchdog"), Reboot(9, "CommandedReboot")]).Should().BeNull("a crash or a restart is not a power fault");
        LanHealthDetectors.PoeCycling(Ev(Fabric(port: powered)), [Reboot(1, "PowerLoss")]).Should().BeNull("one power loss alone");
    }

    [Fact]
    public void PoeCycling_ZeroDrawOnASwitchPoweredPort()
    {
        var incidents = new[] { new LinkIncident { Start = T0.AddHours(1), End = T0.AddHours(1), Kind = IncidentKind.Drop, Explained = ExplainReason.DeviceRebooted, ExplainedDetail = "PowerLoss" } };
        LanHealthDetectors.PoeCycling(Ev(Fabric(port: new PortConfig { PoeMode = "auto" }), s => s.Poe.AddRange([(T0, 9.0), (T0.AddHours(1), 0.0)])), incidents)!
            .Body.Should().Be("AP 1 lost power 1 time, and its PoE draw dropped to 0 W. Check Switch A's PoE budget and the cable.");
    }

    [Fact]
    public void PoeCycling_OnAPortThatSuppliesNoPower_PointsAtTheInjector()
    {
        var incidents = Enumerable.Range(0, 2).Select(h => new LinkIncident { Start = T0.AddHours(h), End = T0.AddHours(h), Kind = IncidentKind.Drop, Explained = ExplainReason.DeviceRebooted, ExplainedDetail = "PowerLoss" }).ToList();
        LanHealthDetectors.PoeCycling(Ev(Fabric(port: new PortConfig { PoeMode = "off" })), incidents)!
            .Body.Should().Be("AP 1 lost power 2 times. Check its injector or power supply, and the cable.");
    }
    // ---- Speed-specific flapping ----

    [Fact]
    public void SpeedSpecificFlapping_DropsOnlyAtTheTopSpeed()
    {
        var drops = Enumerable.Range(0, 3).Select(i => Incident(IncidentKind.Drop, T0.AddHours(20 + i))).ToList();
        var lowerEarlier = new Action<LinkEvidenceBuilderShim>(s => s.Speeds.AddRange([new(T0.AddHours(2), 1000, true, LinkSource.PortTableState), new(T0.AddHours(10), 1000, true, LinkSource.PortTableState)]));
        var f = LanHealthDetectors.SpeedSpecificFlapping(Ev(Fabric(2500), lowerEarlier), drops)!;
        f.Body.Should().Be("The link only drops at 2.5 Gbps. Set the port to 1 Gbps and see if the drops stop.");
        f.Chips.Should().Equal("Drops at 2.5G only");
        var dropWhileSlower = drops.Append(Incident(IncidentKind.Drop, T0.AddHours(5))).ToList();
        LanHealthDetectors.SpeedSpecificFlapping(Ev(Fabric(2500), lowerEarlier), dropWhileSlower).Should().BeNull();
        LanHealthDetectors.SpeedSpecificFlapping(Ev(Fabric(1000), lowerEarlier), drops).Should().BeNull("1G is not a top-speed case");
    }

    // ---- Speed tests ----

    [Fact]
    public void SpeedTestBelowNorm_AveragesTestsOutsideIncidents()
    {
        LinkSpeedTest Test(double down, bool during = false) => new(T0, down, 100, null, "wired-1hop", "ServerToDevice", 900, 900, during);
        LanHealthDetectors.SpeedTestBelowNorm(Ev(Fabric(), s => s.Tests.AddRange([Test(300), Test(350)])))!
            .Body.Should().Be("2 speed tests across this link averaged 36% of what similar paths normally get.");
        LanHealthDetectors.SpeedTestBelowNorm(Ev(Fabric(), s => s.Tests.AddRange([Test(300), Test(100, during: true)]))).Should().BeNull("one test left outside incidents");
        LanHealthDetectors.SpeedTestBelowNorm(Ev(Fabric(), s => s.Tests.AddRange([Test(800), Test(850)]))).Should().BeNull();
    }

    // ---- Mesh, against its own normal ----

    private static LinkRecord MeshLink(int depth = 1, List<MloMember>? mlo = null) => new()
    {
        Id = "mesh", Kind = mlo is { Count: > 1 } ? LinkKind.FabricMeshMlo : LinkKind.FabricMesh,
        Upstream = new LinkEnd { DeviceMac = "aa:bb:cc:00:00:10", DeviceName = "AP Parent", DeviceType = DeviceType.AccessPoint },
        Downstream = new LinkEnd { DeviceMac = "aa:bb:cc:00:00:20", DeviceName = "AP Child", DeviceType = DeviceType.AccessPoint },
        UplinkDepth = depth, Mlo = mlo ?? [],
    };

    private static List<string> MeshClasses(LinkEvidence ev) => LanHealthDetectors.Mesh(ev).Select(f => f.Class).ToList();

    [Fact]
    public void Mesh_SignalAndRateAgainstTheirOwnNormal()
    {
        var ev = Ev(MeshLink(), s =>
        {
            s.MeshBaseline.AddRange(Enumerable.Range(0, 24).Select(i => new MeshReading(T0.AddHours(-i), -50, 866_000, 1, "p1")));
            s.Mesh.AddRange(Enumerable.Range(0, 3).Select(i => new MeshReading(T0.AddHours(i), -60, 300_000, 1, "p1")));
        });
        MeshClasses(ev).Should().Equal("Mesh Signal Drop", "Mesh Rate Drop");
    }

    [Fact]
    public void Mesh_ParentChangesMloLossImbalanceAndDepth()
    {
        var mlo = new List<MloMember> { new("6e", 5, -40, 4_323_600, 4_323_600), new("na", 40, -62, 2_161_800, 1_729_400) };
        var ev = Ev(MeshLink(depth: 2, mlo: mlo), s =>
        {
            s.MeshBaseline.AddRange(Enumerable.Range(0, 24).Select(i => new MeshReading(T0.AddHours(-i), -45, 6_000_000, 2, "p1")));
            s.Mesh.AddRange([new(T0, -45, 6_000_000, 2, "p1"), new(T0.AddHours(1), -45, 6_000_000, 2, "p2"), new(T0.AddHours(2), -45, 6_000_000, 1, "p1")]);
        });
        var f = LanHealthDetectors.Mesh(ev).ToList();
        f.Select(x => x.Class).Should().Equal("Mesh Parent Change", "MLO Member Lost", "MLO Imbalance", "Mesh Depth");
        f[0].Body.Should().Be("The mesh backhaul switched parent APs 2 times in this window.");
        f[1].Body.Should().Be("The MLO backhaul is running on 1 of its normal 2 links.");
        f[2].Points.Should().Be(0, "imbalance is information");
    }

    private static LinkIncident Relink(DateTime at, ExplainReason? why) =>
        new() { Start = at, End = at.AddSeconds(5), Kind = IncidentKind.Relink, Count = 1, Explained = why };

    private static LinkEvidence FailoverAndBack() => Ev(MeshLink(), s =>
        s.Mesh.AddRange([new(T0, -45, 866_000, 1, "p1"), new(T0.AddHours(1), -45, 866_000, 1, "p2"), new(T0.AddHours(2), -45, 866_000, 1, "p1")]));

    [Fact]
    public void Mesh_AParentChangeWhoseRelinksAreAllExplained_IsNotCounted()
    {
        // The parent rebooted in a rollout: the child failed over in one hour and came back in the next.
        var incidents = new[]
        {
            Relink(T0.AddHours(1).AddMinutes(59), ExplainReason.FirmwareRolloutWave),
            Relink(T0.AddHours(2).AddMinutes(3), ExplainReason.FirmwareRolloutWave),
        };
        LanHealthDetectors.Mesh(FailoverAndBack(), incidents).Select(f => f.Class).Should().NotContain("Mesh Parent Change");
    }

    [Fact]
    public void Mesh_AParentChangeWithAnUnexplainedRelink_StillCounts()
    {
        var incidents = new[]
        {
            Relink(T0.AddHours(1).AddMinutes(10), ExplainReason.FirmwareRolloutWave),
            Relink(T0.AddHours(1).AddMinutes(40), null),
            Relink(T0.AddHours(2).AddMinutes(3), ExplainReason.FirmwareRolloutWave),
        };
        LanHealthDetectors.Mesh(FailoverAndBack(), incidents).Single(f => f.Class == "Mesh Parent Change")
            .Body.Should().Be("The mesh backhaul switched parent APs 1 time in this window.");
    }

    [Fact]
    public void Mesh_AParentChangeWithNoRelinkSeen_StillCounts() =>
        LanHealthDetectors.Mesh(FailoverAndBack(), []).Single(f => f.Class == "Mesh Parent Change")
            .Body.Should().Be("The mesh backhaul switched parent APs 2 times in this window.");

    [Fact]
    public void RolloutAffected_TakesTheMeshChildThatFailedOverToAnotherParent()
    {
        // Back Yard is upgrading; Front Yard (and the AP behind it) now hang off Main Kitchen.
        LinkRecord Mesh(string up, string down) => new()
        {
            Id = $"{up}|{down}", Kind = LinkKind.FabricMesh,
            Upstream = new LinkEnd { DeviceMac = up, DeviceType = DeviceType.AccessPoint },
            Downstream = new LinkEnd { DeviceMac = down, DeviceType = DeviceType.AccessPoint },
        };
        const string backYard = "aa:bb:cc:00:00:01", kitchen = "aa:bb:cc:00:00:02", frontYard = "aa:bb:cc:00:00:03", behind = "aa:bb:cc:00:00:04";
        var topology = new LanHealthEngine.Topology { Links = [Mesh(kitchen, frontYard), Mesh(frontYard, behind)] };

        LanHealthEngine.RolloutAffected(backYard, topology, []).Should().BeEquivalentTo([backYard]);
        LanHealthEngine.RolloutAffected(backYard, topology, [frontYard]).Should().BeEquivalentTo([backYard, frontYard, behind]);
        LanHealthEngine.RolloutAffected(backYard, topology, [frontYard]).Should().NotContain(kitchen, "the new parent did not reboot");
    }

    [Fact]
    public void UniFiOsUpdate_ExplainsTheConsoleAndEveryDeviceBelowIt()
    {
        // A UniFi OS-only plan writes no step row; its downtime lives in the plan document.
        const string gw = "aa:bb:cc:00:00:01", ap = "aa:bb:cc:00:00:02", other = "aa:bb:cc:00:00:09";
        var topology = new LanHealthEngine.Topology
        {
            Devices = [new() { Mac = gw, Type = "udm" }, new() { Mac = ap, Type = "uap" }],
            Links = [new LinkRecord
            {
                Id = $"{gw}|{ap}", Kind = LinkKind.FabricWired,
                Upstream = new LinkEnd { DeviceMac = gw, DeviceType = DeviceType.Gateway },
                Downstream = new LinkEnd { DeviceMac = ap, DeviceType = DeviceType.AccessPoint },
            }],
        };
        var down = T0.AddMinutes(2);
        var doc = new NetworkOptimizer.Web.Services.Firmware.RolloutPlanDocument { IncludesUniFiOsUpdate = true, ConsoleMac = "AA:BB:CC:00:00:01" };
        doc.UniFiOsUpdate.WentDownAt = down;
        doc.UniFiOsUpdate.BackAt = down.AddMinutes(4);
        doc.UniFiOsUpdate.TargetVersion = "6.0.19";
        var plan = new NetworkOptimizer.Storage.Models.FirmwareRolloutPlan { PlanJson = System.Text.Json.JsonSerializer.Serialize(doc), StartedAt = T0, CompletedAt = down.AddMinutes(5) };

        var windows = LanHealthEngine.UniFiOsUpdateWindows([plan], topology, T0.AddHours(1)).ToList();

        windows.Select(w => w.DeviceMac).Should().BeEquivalentTo([gw, ap]).And.NotContain(other);
        windows.Should().OnlyContain(w => w.Reason == ExplainReason.FirmwareRolloutWave && w.Detail == "6.0.19");
        windows[0].Covers(down.AddSeconds(4), down.AddSeconds(15)).Should().BeTrue("a drop while the gateway reboots");
        windows[0].Covers(down.AddMinutes(20), down.AddMinutes(20)).Should().BeFalse();

        var serverHosted = new LanHealthEngine.Topology { Devices = [topology.Devices[1]], Links = topology.Links };
        LanHealthEngine.UniFiOsUpdateWindows([plan], serverHosted, T0.AddHours(1)).Should().BeEmpty("a UniFi OS Server host is not a gateway");

        doc.IncludesUniFiOsUpdate = false;
        plan.PlanJson = System.Text.Json.JsonSerializer.Serialize(doc);
        LanHealthEngine.UniFiOsUpdateWindows([plan], topology, T0.AddHours(1)).Should().BeEmpty("a Network app update only restarts the app");
    }

    [Fact]
    public void Mesh_NothingOnAWiredLink() => MeshClasses(Ev(Fabric())).Should().BeEmpty();

    [Fact]
    public void LinkDrops_CostLessWhenTheLinkIsNormallyIdle()
    {
        // Busy 09:00 to 17:00 UTC, idle otherwise, over two days of baseline.
        LinkHourSample H(int i) { var at = T0.AddHours(i); return new(at, null, null, at.Hour is >= 9 and < 17 ? 500e6 : 0, 0, null, null, null, null, null); }
        var utc = TimeZoneInfo.Utc;
        var profile = LanHealthScorer.UsageProfile(Enumerable.Range(-48, 48).Select(H), utc);
        LanHealthScorer.UsageWeight(profile, T0.AddHours(12), utc).Should().Be(1.0);
        LanHealthScorer.UsageWeight(profile, T0.AddHours(3), utc).Should().Be(LanHealthScorer.UsageWeightFloor);
        LanHealthScorer.UsageProfile(Enumerable.Range(0, 10).Select(H), utc).Should().BeNull("under a day of hours");
        LanHealthScorer.UsageWeight(null, T0.AddHours(3), utc).Should().Be(1.0);
    }

    [Fact]
    public void LinkDrops_NameDebounceWhenTheSwitchMissedDrops_AndFlagTheEeeTest()
    {
        LinkIncident Drop(int h, bool switchMissed)
        {
            var i = new LinkIncident { Start = T0.AddHours(h), End = T0.AddHours(h).AddSeconds(3), Kind = IncidentKind.Drop };
            i.SeenBy.Add(LinkSource.ApKernelLog);
            if (switchMissed) i.MissedBy.Add(LinkSource.SwitchLinkDownCount);
            return i;
        }
        var port = new PortConfig { LinkDebounce = "Auto", Eee = true };
        var ev = Ev(Fabric(port: port), s => s.Hours.AddRange(Enumerable.Range(0, 6).Select(i => Hour(i, meanIn: i < 4 ? 50_000 : 400e6))));
        var f = LanHealthDetectors.LinkDrops(ev, [Drop(1, true), Drop(2, true), Drop(3, false), Drop(5, false)], FindingText.Utc)!;
        f.Body.Should().Contain("Link Debounce is Auto on this port, so the switch's own drop count can miss short drops.");
        f.EeeTest.Should().BeTrue();
        f.IdleDrops.Should().BeTrue("three of four drops came in idle hours");
        var quiet = LanHealthDetectors.LinkDrops(Ev(Fabric(port: new PortConfig { LinkDebounce = "Off" })), [Drop(1, true), Drop(2, true), Drop(3, true)], FindingText.Utc)!;
        quiet.Body.Should().NotContain("Link Debounce");
        quiet.EeeTest.Should().BeFalse();
    }

    [Fact]
    public void SpeedKeepsDropping_UsesConfigOptimizersWanCheckOnTheLinksHourlySpeeds()
    {
        var link = Fabric(2500);
        // Two drops to 1G, each held three hours, over a week of hourly minimums; a one-hour blip does not count.
        var hours = Enumerable.Range(0, 168).Select(i => (link.Id, T0.AddHours(i), i is >= 10 and < 13 or >= 50 and < 53 ? 1000 : i == 90 ? 100 : 2500)).ToList();
        var history = LanHealthReportBuilder.SpeedHistories([link], hours)[link.Id];
        history.DegradedEpisodes.Should().Be(2);
        var ev = new LinkEvidence { Link = link, SpeedHistory = history };
        LanHealthDetectors.SpeedKeepsDropping(ev, null)!.Body.Should().Be(
            "Dropped below its usual 2.5 Gbps 2 times in the last 7 days, as low as 1 Gbps, for about 6 hours in total. Set the port's speed to 2.5 Gbps instead of auto-negotiation. If it still drops, replace the cable.");
        LanHealthDetectors.SpeedKeepsDropping(new LinkEvidence { Link = Fabric(2500, forced: true), SpeedHistory = history }, null)!.Body
            .Should().EndWith("The port's speed is already set manually: check or replace the cable.");
        LanHealthDetectors.SpeedKeepsDropping(ev, new LinkFinding()).Should().BeNull("a link below its usual speed now is the downshift finding's");
    }
}