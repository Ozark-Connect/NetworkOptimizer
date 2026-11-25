using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.Monitoring.LanHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services.Monitoring.LanHealth;

/// <summary>The site-wide checks: each fires on its signature and stays quiet on its near miss.</summary>
public class SiteDetectorTests
{
    private static readonly DateTime T0 = new(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);
    private const string GwMac = "aa:bb:cc:00:00:01", CoreMac = "aa:bb:cc:00:00:02", EdgeMac = "aa:bb:cc:00:00:03", ApMac = "aa:bb:cc:00:00:04";

    private static UniFiDeviceResponse Switch(string mac, string name, int? priority = null, string? version = null, int? depth = 1,
        List<SwitchPort>? ports = null, string? overridesJson = null, string? uplinkType = "wire", string fw = "7.1.26", string model = "US24") => new()
    {
        Mac = mac, Name = name, Type = "usw", Model = model, Adopted = true, StpPriority = priority, StpVersion = version, UplinkDepth = depth,
        Version = fw, PortTable = ports ?? [], Uplink = uplinkType == null ? null : new UplinkInfo { Type = uplinkType },
        AdditionalData = overridesJson == null ? null : new Dictionary<string, JsonElement> { ["port_overrides"] = JsonDocument.Parse(overridesJson).RootElement.Clone() },
    };

    private static UniFiDeviceResponse Gateway() => new() { Mac = GwMac, Name = "Gateway A", Type = "uxg", Model = "UXGA6AA", Adopted = true };

    private static LinkHealth Link(string id, string upMac, string upName, int port, DeviceType down, string downName = "Device", int speed = 1000,
        PortConfig? upPort = null, PortConfig? downPort = null, LinkKind kind = LinkKind.FabricWired, bool upJumbo = true, bool downJumbo = true) => new()
    {
        Link = new LinkRecord
        {
            Id = id, Kind = kind, SpeedMbps = speed,
            Upstream = new LinkEnd { DeviceMac = upMac, DeviceName = upName, DeviceType = upMac == GwMac ? DeviceType.Gateway : DeviceType.Switch, PortIdx = port, HasJumboSetting = upJumbo },
            Downstream = new LinkEnd { DeviceMac = id + "-dn", DeviceName = downName, DeviceType = down, HasJumboSetting = downJumbo },
            UpstreamPort = upPort ?? new PortConfig(), DownstreamPort = downPort, ClientsBehind = 4,
        },
    };

    private static List<string> Classes(IEnumerable<LinkFinding> f) => f.Select(x => x.Class).ToList();

    // ---- Loop and STP churn ----

    [Fact]
    public void Loop_StormsOnSeveralPortsWithStpChurn()
    {
        LinkHealth Stormy(string id) { var l = Link(id, CoreMac, "Core", 1, DeviceType.Switch); l.Findings.Add(new LinkFinding { LinkId = id, Class = "Broadcast Storm" }); return l; }
        var churn = new Dictionary<string, (double, double)> { [CoreMac] = (40, 0.1) };
        var f = LanSiteDetectors.Loop(new SiteEvidence { Links = [Stormy("a"), Stormy("b")], StpChanges = churn }).Single();
        f.Class.Should().Be("Network Loop");
        f.Title.Should().Be("Network Loop: 2 ports");
        LanSiteDetectors.Loop(new SiteEvidence { Links = [Stormy("a")], StpChanges = churn }).Should().BeEmpty("one port is a storm");
        LanSiteDetectors.Loop(new SiteEvidence { Links = [Stormy("a"), Stormy("b")] }).Should().BeEmpty("no STP churn");
    }

    [Fact]
    public void StpChurn_ThreeTimesItsNormalAndAtLeastTen()
    {
        var s = new SiteEvidence { Devices = [Switch(CoreMac, "Core")], StpChanges = new Dictionary<string, (double, double)> { [CoreMac] = (30, 0.1) } };
        LanSiteDetectors.StpChurn(s).Single().Body.Should().StartWith("30 STP state changes in 48h, against 5 normally.");
        LanSiteDetectors.StpChurn(new SiteEvidence { StpChanges = new Dictionary<string, (double, double)> { [CoreMac] = (9, 0) } }).Should().BeEmpty();
        LanSiteDetectors.StpChurn(new SiteEvidence { StpChanges = new Dictionary<string, (double, double)> { [CoreMac] = (30, 0.5) } }).Should().BeEmpty("24 expected");
    }

    // ---- STP configuration ----

    [Fact]
    public void StpConfig_RootLeftToChance_AndMixedVersions()
    {
        var c = Classes(LanSiteDetectors.StpConfig(new SiteEvidence { Devices = [Switch(CoreMac, "Core", version: "rstp"), Switch(EdgeMac, "Edge", version: "stp")] }));
        c.Should().Contain(["STP Root Left to Chance", "Mixed STP Versions"]);
        Classes(LanSiteDetectors.StpConfig(new SiteEvidence { Devices = [Switch(CoreMac, "Core", priority: 4096), Switch(EdgeMac, "Edge")] })).Should().BeEmpty();
    }

    [Fact]
    public void StpConfig_RootPlacement_BehindMeshOrDeep()
    {
        var mesh = LanSiteDetectors.StpConfig(new SiteEvidence { Devices = [Switch(EdgeMac, "Edge", priority: 4096, uplinkType: "wireless"), Switch(CoreMac, "Core")] })
            .Single(f => f.Class == "STP Root Placement");
        mesh.Body.Should().Be("Edge is the STP root, behind a mesh link. The root should be the core switch next to the gateway.");
        LanSiteDetectors.StpConfig(new SiteEvidence { Devices = [Switch(EdgeMac, "Edge", priority: 4096, depth: 3), Switch(CoreMac, "Core")] })
            .Single(f => f.Class == "STP Root Placement").Body.Should().Contain(", 3 hops from the gateway.");
    }

    [Fact]
    public void StpConfig_PortChecks()
    {
        var ports = new List<SwitchPort>
        {
            new() { PortIdx = 1, Up = true, StpEdgePort = false },                         // fabric to a switch, configured Edge
            new() { PortIdx = 2, Up = true },                                              // fabric to a 5G backup, Edge: a leaf, fine
            new() { PortIdx = 3, Up = true },                                              // access, forced Infrastructure
            new() { PortIdx = 4, Up = true, StpEdgePort = true },                          // access, Edge, BPDU guard off
            new() { PortIdx = 5, Up = true, StpState = "blocking" },                       // blocked
            new() { PortIdx = 6, Up = true, StpPathCost = 50000 },                         // 10G with a high cost
        };
        var overrides = """[{"port_idx":1,"stp_edge_state":"enabled"},{"port_idx":2,"stp_edge_state":"enabled"},{"port_idx":3,"stp_edge_state":"disabled"},{"port_idx":4,"stp_bpdu_guard_enabled":false}]""";
        var s = new SiteEvidence
        {
            Devices = [Switch(CoreMac, "Core", priority: 4096, ports: ports, overridesJson: overrides)],
            Links =
            [
                Link("p1", CoreMac, "Core", 1, DeviceType.Switch), Link("p2", CoreMac, "Core", 2, DeviceType.CellularModem),
                Link("p3", CoreMac, "Core", 3, DeviceType.Unknown, kind: LinkKind.Access), Link("p4", CoreMac, "Core", 4, DeviceType.Unknown, kind: LinkKind.Access),
                Link("p5", CoreMac, "Core", 5, DeviceType.Switch), Link("p6", CoreMac, "Core", 6, DeviceType.Switch, speed: 10000),
            ],
        };
        var f = LanSiteDetectors.StpConfig(s).ToList();
        f.Select(x => (x.Class, x.LinkId)).Should().BeEquivalentTo(new[]
        {
            ("Edge Port on a Fabric Link", "p1"), ("Infrastructure Mode on an Access Port", "p3"), ("BPDU Guard Off", "p4"),
            ("Blocked Port", "p5"), ("STP Path Cost", "p6"),
        });
    }

    [Fact]
    public void StpConfig_DisabledWithRedundancy()
    {
        var sw = Switch(CoreMac, "Core", priority: 4096, version: "disabled");
        sw.LldpTable = [new UniFiLldpEntry { IsWired = true }, new UniFiLldpEntry { IsWired = true }];
        LanSiteDetectors.StpConfig(new SiteEvidence { Devices = [sw] }).Single().Body
            .Should().Be("STP is off on Core, and LLDP shows 2 neighbors. A second path between switches will loop.");
    }

    // ---- LACP ----

    [Fact]
    public void Lacp_MemberDownSpeedMismatchAndImbalance()
    {
        var sw = Switch(CoreMac, "Core", ports:
        [
            new() { PortIdx = 9, LagIdx = 1, Enable = true, Up = true, Speed = 10000, TxBytes = 200_000_000_000 },
            new() { PortIdx = 10, LagIdx = 1, Enable = true, Up = true, Speed = 1000, TxBytes = 1_000_000_000 },
            new() { PortIdx = 11, LagIdx = 1, Enable = true, Up = false, Speed = 0 },
        ]);
        var f = LanSiteDetectors.Lacp(new SiteEvidence { Devices = [sw] }).ToList();
        f.Select(x => x.Class).Should().Equal("LACP Member Down", "LACP Speed Mismatch", "LACP Imbalance");
        f[0].Body.Should().Be("1 of 3 aggregate members are down (Port 11).");
        f[0].Points.Should().Be(30);
    }

    // ---- VLAN ----

    [Fact]
    public void NativeVlanMismatch_NamesBothNetworks()
    {
        var link = Link("t", CoreMac, "Core", 1, DeviceType.Switch, "Edge", upPort: new PortConfig { NativeNetworkId = "n1" }, downPort: new PortConfig { NativeNetworkId = "n2" });
        var f = LanSiteDetectors.NativeVlanMismatch(new SiteEvidence
        {
            Links = [link],
            Networks = [new UniFiNetworkConfig { Id = "n1", Name = "Default" }, new UniFiNetworkConfig { Id = "n2", Name = "IoT" }],
        }).Single();
        f.Body.Should().Be("The two ends use different native networks: Default on Core, IoT on Edge.");
        LanSiteDetectors.NativeVlanMismatch(new SiteEvidence { Links = [Link("t", CoreMac, "Core", 1, DeviceType.Switch, upPort: new PortConfig { NativeNetworkId = "n1" }, downPort: new PortConfig { NativeNetworkId = "n1" })] })
            .Should().BeEmpty();
    }

    // ---- Multicast ----

    private static JsonDocument Igmp(bool enabled, string querier, params string[] networks) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new { data = new object[] { new { key = "igmp_snooping", enabled, network_ids = networks, querier_mode = querier } } }));

    [Fact]
    public void Multicast_FloodingWithSnoopingOff()
    {
        var heavy = new LinkEvidence { Link = Link("m", CoreMac, "Core", 1, DeviceType.Switch).Link, Hours = [new(T0, null, null, null, null, null, 900, 1000, null, null)] };
        SiteEvidence S(JsonDocument settings) => new()
        {
            Evidence = new Dictionary<string, LinkEvidence> { ["m"] = heavy },
            Networks = [new UniFiNetworkConfig { Id = "n1", Name = "Default", Purpose = "corporate" }],
            Settings = settings,
        };
        LanSiteDetectors.MulticastHygiene(S(Igmp(false, "OFF"))).Single().Body
            .Should().Be("Heavy multicast on 1 link with IGMP snooping off, so every port receives every stream.");
        LanSiteDetectors.MulticastHygiene(S(Igmp(true, "PRIMARY_AND_FAILOVER", "n1"))).Should().BeEmpty("snooping is on for the network");
    }

    [Fact]
    public void NoIgmpQuerier_SnoopingOnWithTheQuerierOff()
    {
        var networks = new List<UniFiNetworkConfig> { new() { Id = "n1", Name = "Default", Purpose = "corporate" } };
        LanSiteDetectors.MulticastHygiene(new SiteEvidence { Networks = networks, Settings = Igmp(true, "OFF", "n1") }).Single().Class.Should().Be("No IGMP Querier");
        LanSiteDetectors.MulticastHygiene(new SiteEvidence { Networks = networks, Settings = Igmp(true, "PRIMARY_AND_FAILOVER", "n1") }).Should().BeEmpty();
        LanSiteDetectors.MulticastHygiene(new SiteEvidence { Networks = networks, Settings = Igmp(false, "OFF") }).Should().BeEmpty("no snooping, no querier needed");
    }
    // ---- Hairpin, management plane, device health ----

    [Fact]
    public void Hairpin_SymmetricTrunkLoadWithABusyGatewayCpu()
    {
        var trunk = Link("gw-core", GwMac, "Gateway A", 2, DeviceType.Switch, "Core", speed: 1000);
        var hours = Enumerable.Range(0, 3).Select(i => new LinkHourSample(T0.AddHours(i), 600e6, 550e6, 600e6, 550e6, null, null, null, null, null)).ToList();
        var ev = new LinkEvidence { Link = trunk.Link, Hours = hours };
        SiteEvidence S(double cpu) => new()
        {
            Devices = [Gateway()], Links = [trunk], Evidence = new Dictionary<string, LinkEvidence> { [trunk.Link.Id] = ev },
            DeviceHealth = new Dictionary<string, (double?, double?, double?)> { [GwMac] = (cpu, null, null) },
        };
        LanSiteDetectors.HairpinLoad(S(85)).Single().Body.Should().StartWith("For 3 hours the gateway trunk ran nearly equal both ways with the gateway CPU at 85%:");
        LanSiteDetectors.HairpinLoad(S(40)).Should().BeEmpty();
    }

    [Fact]
    public void ConsoleReachability_InformFailuresOnALinkThatStayedUp()
    {
        var link = Link("ap", CoreMac, "Core", 1, DeviceType.AccessPoint, "AP 1");
        var s = new SiteEvidence { Devices = [new UniFiDeviceResponse { Mac = "ap-dn", Name = "AP 1", Type = "uap" }], Links = [link], InformFailures = new Dictionary<string, int> { ["ap-dn"] = 4 } };
        LanSiteDetectors.ManagementPlane(s).Single().Body.Should().StartWith("4 inform failures to the UniFi Console in 48h, while its LAN link stayed up.");
        LanSiteDetectors.ManagementPlane(new SiteEvidence { InformFailures = new Dictionary<string, int> { ["ap-dn"] = 2 } }).Should().BeEmpty();
    }

    [Fact]
    public void SwitchHealth_AndFirmwareDrift()
    {
        var s = new SiteEvidence
        {
            Devices = [Switch(CoreMac, "Core", fw: "7.1.26"), Switch(EdgeMac, "Edge", fw: "7.2.120")],
            DeviceHealth = new Dictionary<string, (double?, double?, double?)> { [CoreMac] = (95, 50, 82) },
        };
        var f = LanSiteDetectors.FabricDeviceHealth(s).ToList();
        f[0].Body.Should().Be("CPU is at 95%. Temperature is 82 °C.");
        f[1].Class.Should().Be("Firmware Drift");
        f[1].Body.Should().Be("2 devices of the same model are running 2 different firmware versions (7.1.26, 7.2.120).");
    }

    [Fact]
    public void FirmwareDrift_OneBuildWithAndWithoutItsBuildNumberIsNotDrift()
    {
        var s = new SiteEvidence { Devices = [Switch(CoreMac, "Core", fw: "6.5.87.14874"), Switch(EdgeMac, "Edge", fw: "6.5.87")] };
        LanSiteDetectors.FabricDeviceHealth(s).Should().NotContain(f => f.Class == "Firmware Drift");
    }

    // ---- L2 / IP ----

    [Fact]
    public void MacFlapping_AndDuplicateIp()
    {
        var s = new SiteEvidence
        {
            MacMoves = [("aa:bb:cc:00:00:50", "NAS", 4, new[] { "Core Port 3", "Edge Port 7" }), ("aa:bb:cc:00:00:51", "PC", 2, new[] { "a", "b" })],
            DuplicateIps = [("192.0.2.10", new[] { "aa:bb:cc:00:00:60", "aa:bb:cc:00:00:61" })],
        };
        LanSiteDetectors.MacMoves(s).Single().Body.Should().StartWith("NAS moved between Core Port 3 and Edge Port 7 4 times in 48h.");
        LanSiteDetectors.DuplicateIps(s).Single().Body.Should().Be("2 devices are answering for 192.0.2.10 (aa:bb:cc:00:00:60, aa:bb:cc:00:00:61).");
    }

    // ---- Capacity ----

    [Fact]
    public void CapacityTrend_GrowingTowardHalfTheLink()
    {
        var link = Link("gw-core", GwMac, "Gateway A", 2, DeviceType.Switch, "Core", speed: 1000);
        // Three weeks of baseline plus the window, busy hours growing about 8 points a week from 20%.
        var start = T0.AddDays(-21);
        LinkHourSample H(int hour)
        {
            var week = hour / (24 * 7);
            return new LinkHourSample(start.AddHours(hour), null, null, (0.2 + 0.08 * week) * 1e9, null, null, null, null, null, null);
        }
        var ev = new LinkEvidence { Link = link.Link, BaselineHours = Enumerable.Range(0, 21 * 24).Select(H).ToList(), Hours = Enumerable.Range(21 * 24, 48).Select(H).ToList() };
        var f = LanSiteDetectors.CapacityTrend(new SiteEvidence { Evidence = new Dictionary<string, LinkEvidence> { [link.Link.Id] = ev } }).Single();
        f.Body.Should().StartWith("Busy-hour load on this link is growing about 8 points a week. If that continues, it will pass 50% of the link in about");
        var flat = new LinkEvidence { Link = link.Link, BaselineHours = Enumerable.Range(0, 21 * 24).Select(i => new LinkHourSample(start.AddHours(i), null, null, 2e8, null, null, null, null, null, null)).ToList() };
        LanSiteDetectors.CapacityTrend(new SiteEvidence { Evidence = new Dictionary<string, LinkEvidence> { ["x"] = flat } }).Should().BeEmpty();
    }

    [Fact]
    public void CapacityTrend_HoursBeforeRatesWereCollected_AreNotGrowth()
    {
        // A mesh link with no rates for two weeks, then a steady 10%: unknown hours are not 0% load.
        var link = Link("gw-core", GwMac, "Gateway A", 2, DeviceType.Switch, "Core", speed: 1000);
        var start = T0.AddDays(-21);
        LinkHourSample H(int hour) => new(start.AddHours(hour), null, null, hour < 14 * 24 ? null : 1e8, null, null, null, null, null, null);
        var ev = new LinkEvidence { Link = link.Link, BaselineHours = Enumerable.Range(0, 21 * 24).Select(H).ToList(), Hours = Enumerable.Range(21 * 24, 48).Select(H).ToList() };
        LanSiteDetectors.CapacityTrend(new SiteEvidence { Evidence = new Dictionary<string, LinkEvidence> { [link.Link.Id] = ev } }).Should().BeEmpty();
    }

    [Fact]
    public void GatewayForwarding_TestsWellBelowNorm()
    {
        var s = new SiteEvidence { Devices = [Gateway()], Links = [Link("gw-core", GwMac, "Gateway A", 2, DeviceType.Switch)], GatewayForwarding = (3, 0.4) };
        var f = LanSiteDetectors.GatewayForwarding(s).Single();
        f.Body.Should().StartWith("3 LAN speed tests through the gateway averaged 40% of normal.");
        f.LinkId.Should().Be("gw-core");
        f.Points.Should().Be(20);
        LanSiteDetectors.GatewayForwarding(new SiteEvidence { GatewayForwarding = (3, 0.7) }).Should().BeEmpty();
        LanSiteDetectors.GatewayForwarding(new SiteEvidence { GatewayForwarding = (1, 0.2) }).Should().BeEmpty("one test is not a trend");
    }

    // ---- Exclusion suggestion ----

    [Fact]
    public void ChurnByDesign_MostPortsOnAProfileRelink()
    {
        LinkHealth Port(int i, int drops)
        {
            var l = Link($"a{i}", CoreMac, "Core", i, DeviceType.Unknown, kind: LinkKind.Access, upPort: new PortConfig { PortProfileId = "guest" });
            for (var d = 0; d < drops; d++) l.Incidents.Add(new LinkIncident { Start = T0.AddHours(d), End = T0.AddHours(d), Kind = IncidentKind.Drop });
            return l;
        }
        var s = new SiteEvidence { Links = [Port(1, 8), Port(2, 7), Port(3, 9), Port(4, 0)], PortProfiles = [new UniFiPortProfile { Id = "guest", Name = "Guest Rooms" }] };
        LanSiteDetectors.ProfileChurn(s).Single().Body.Should().StartWith("3 of 4 access ports on the Guest Rooms profile relink many times a day.");
        var quiet = new SiteEvidence { Links = [Port(1, 8), Port(2, 0), Port(3, 0), Port(4, 0)] };
        LanSiteDetectors.ProfileChurn(quiet).Should().BeEmpty();
    }

    [Fact]
    public void Run_ZeroesThePointsOfSuggestionOnlyChecks()
    {
        var s = new SiteEvidence { Devices = [Switch(CoreMac, "Core", version: "rstp"), Switch(EdgeMac, "Edge", version: "stp")] };
        LanSiteDetectors.Run(s).Should().OnlyContain(f => f.Points == 0);
    }
}
