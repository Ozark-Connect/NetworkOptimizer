using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.ApAgent;
using NetworkOptimizer.Web.Services.Monitoring.LanHealth;
using Xunit;
using static NetworkOptimizer.Storage.Services.MonitoringInfluxClient;

namespace NetworkOptimizer.Web.Tests.Services.Monitoring.LanHealth;

public class LanHealthTests
{
    private const string GatewayMac = "aa:bb:cc:00:00:01";
    private const string SwitchMac = "aa:bb:cc:00:00:02";
    private const string ApMac = "aa:bb:cc:00:00:03";
    private const string Target = "fabric-ap";

    private static LinkRecord ApLink(bool upstreamIsSwitch = false) => new()
    {
        Id = upstreamIsSwitch ? $"{SwitchMac}|5-{ApMac}|1" : $"{GatewayMac}|3-{ApMac}|1",
        Kind = LinkKind.FabricWired,
        Upstream = upstreamIsSwitch
            ? new LinkEnd { DeviceMac = SwitchMac, DeviceName = "Switch A", DeviceType = DeviceType.Switch, PortIdx = 5, PortName = "Port 5" }
            : new LinkEnd { DeviceMac = GatewayMac, DeviceName = "Gateway A", DeviceType = DeviceType.Gateway, PortIdx = 3, IfName = "eth2", PortName = "Port 3" },
        Downstream = new LinkEnd { DeviceMac = ApMac, DeviceName = "AP 1", DeviceType = DeviceType.AccessPoint, PortIdx = 1 },
        SpeedMbps = 2500,
        FullDuplex = true,
        ClientsBehind = 5,
        DevicesBehind = 1,
    };

    private static LanHealthEvidenceBuilder.HourInputs Inputs(DateTime hour, IEnumerable<DateTime> drops, IEnumerable<DateTime> probeCaught,
        IEnumerable<PortFieldStats>? ports = null) => new()
    {
        Hour = hour,
        Events = drops.SelectMany(t => new[]
        {
            new LanEventPoint(t, ApMac, ApAgentLinkEventSink.EventTypes.LinkDown, "eth0", null, null, null),
            new LanEventPoint(t.AddSeconds(3), ApMac, ApAgentLinkEventSink.EventTypes.LinkUp, "eth0", null, "2500", null),
        }).ToList(),
        Loss = probeCaught.Select(t => new ProbeLossPoint(t.AddSeconds(4), Target, 100)).ToList(),
        Probes = new Dictionary<string, (int, double)> { [Target] = (720, 0.04) },
        TargetDevices = new Dictionary<string, string> { [Target] = ApMac },
        Ports = (ports ?? [new PortFieldStats(hour, ApMac, "eth0", "eth0", "oper_status", 1, 1, 1, 1, null)]).ToList(),
    };

    private static (Dictionary<string, LinkRecord>, Dictionary<string, HashSet<string>>) Tree(LinkRecord link) =>
        (new() { [ApMac] = link }, new() { [ApMac] = [ApMac] });

    [Fact]
    public void ReferenceReplay_AllDropsFound_OneIncidentEach_CoverageMatches()
    {
        // 30 drops of about 3 s over nine days; the probe catches 26, SNMP link state catches none.
        var link = ApLink();
        var (downstream, subtree) = Tree(link);
        var start = new DateTime(2026, 1, 13, 1, 23, 0, DateTimeKind.Utc);
        var drops = Enumerable.Range(0, 30).Select(i => start.AddMinutes(i * 431)).ToList();
        var missed = drops.Where((_, i) => i % 8 == 3).Take(4).ToHashSet();
        var incidents = new List<LinkIncident>();
        var rows = new List<NetworkOptimizer.Storage.Models.LanHealthLinkHour>();
        foreach (var hour in drops.Select(d => LanHealthEngine.FloorHour(d)).Distinct())
        {
            var inHour = drops.Where(d => LanHealthEngine.FloorHour(d) == hour).ToList();
            var built = LanHealthEvidenceBuilder.BuildHour(link, Inputs(hour, inHour, inHour.Where(d => !missed.Contains(d))), downstream, subtree, 1);
            incidents.AddRange(built.Incidents);
            rows.Add(built.Row);
        }

        incidents.Where(i => i.Kind == IncidentKind.Drop).Should().HaveCount(30);
        incidents.Should().OnlyContain(i => i.Kind == IncidentKind.Drop);
        incidents.Should().OnlyContain(i => i.DownSeconds == 3);
        var coverage = LanHealthReportBuilder.CoverageFromRows(rows).ToDictionary(c => c.Source);
        coverage[LinkSource.ApKernelLog].Should().Match<SourceCoverage>(c => c.Caught == 30 && c.Total == 30);
        coverage[LinkSource.FabricProbe].Should().Match<SourceCoverage>(c => c.Caught == 26 && c.Total == 30);
        coverage[LinkSource.SnmpOperStatus].Should().Match<SourceCoverage>(c => c.Caught == 0 && c.Total == 30);
    }

    [Fact]
    public void ReferenceReplay_FindingNamesTheSignature()
    {
        var link = ApLink();
        var now = new DateTime(2026, 1, 22, 0, 0, 0, DateTimeKind.Utc);
        var incidents = Enumerable.Range(0, 10).Select(i =>
        {
            var t = now.AddHours(-40 + i * 4);
            var inc = new LinkIncident { Start = t, End = t.AddSeconds(3), Kind = IncidentKind.Drop, SpeedAfterMbps = 2500 };
            inc.Events.Add(new LinkEvent(t, t, LinkEventType.LinkDown, LinkSource.ApKernelLog, LinkSide.Downstream));
            inc.Events.Add(new LinkEvent(t.AddSeconds(3), t.AddSeconds(3), LinkEventType.LinkUp, LinkSource.ApKernelLog, LinkSide.Downstream, SpeedMbps: 2500));
            return inc;
        }).ToList();
        var ev = new LinkEvidence
        {
            Link = link, From = now.AddHours(-48), To = now, HistoryDays = 14,
            Upstream = new EndCounters { ErrorsBlind = true },
            Downstream = new EndCounters { RxErrors = 15 },
        };
        var f = LanHealthDetectors.LinkDrops(ev, incidents, FindingText.Utc)!;
        f.Title.Should().Be("Link Flapping: Gateway A Port 3 to AP 1");
        f.Body.Should().StartWith("10 drops in 48h, about 3 seconds each; the link comes back at 2.5 Gbps every time.");
        f.Body.Should().Contain("with none in the 14 days before.");
        f.Body.Should().EndWith("Errors only on the AP 1 side.");
        f.Chips.Should().Contain("Short link drops").And.Contain("Fabric · 5 clients behind it");
        f.Points.Should().BeGreaterThan(0);
    }

    [Fact]
    public void LinkDownCount_Present_CountsDropsAndCovers()
    {
        var link = ApLink(upstreamIsSwitch: true);
        var (downstream, subtree) = Tree(link);
        var hour = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var ports = new[]
        {
            new PortFieldStats(hour, SwitchMac, "Port 5", "pt:5", "link_down_count", 10, 13, 10, 13, null),
            new PortFieldStats(hour, SwitchMac, "Port 5", "pt:5", "pt_rx_errors", 0, 0, 0, 0, null),
        };
        var built = LanHealthEvidenceBuilder.BuildHour(link, Inputs(hour, [], [], ports), downstream, subtree, 1);

        built.Incidents.Should().ContainSingle(i => i.Kind == IncidentKind.Relink && i.Count == 3);
        built.Row.Relinks.Should().Be(3);
        built.Row.UpLinkDownCount.Should().Be(13);
        built.Evidence.Observers.Should().Contain(o => o.Source == LinkSource.SwitchLinkDownCount && o.Observing.Count == 1);
    }

    [Fact]
    public void LinkDownCount_Missing_IsNotReported_NeverZeroDrops_FallsBackToOtherSources()
    {
        // A Flex Mini-class switch: port counters present, no link_down_count.
        var link = ApLink(upstreamIsSwitch: true);
        var (downstream, subtree) = Tree(link);
        var hour = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var ports = new[]
        {
            new PortFieldStats(hour, SwitchMac, "Port 5", "pt:5", "pt_rx_errors", 0, 0, 0, 0, null),
            new PortFieldStats(hour, ApMac, "eth0", "eth0", "last_change", 100, 900, 100, 900, null),
        };
        var drops = new[] { hour.AddMinutes(10), hour.AddMinutes(40) };
        var built = LanHealthEvidenceBuilder.BuildHour(link, Inputs(hour, drops, drops, ports), downstream, subtree, 1);

        built.Row.UpLinkDownCount.Should().BeNull();
        var coverage = LanHealthReportBuilder.CoverageFromRows([built.Row]).ToDictionary(c => c.Source);
        coverage[LinkSource.SwitchLinkDownCount].Observing.Should().BeFalse();
        coverage[LinkSource.SwitchLinkDownCount].Total.Should().Be(0);
        // The drops still come from the far end's kernel log and the probes, and ifLastChange confirms them.
        built.Incidents.Where(i => i.Kind == IncidentKind.Drop).Should().HaveCount(2);
        built.Incidents.Should().NotContain(i => i.Kind == IncidentKind.Relink);
        built.Incidents.Should().Contain(i => i.SeenBy.Contains(LinkSource.SnmpLastChange));
        coverage[LinkSource.ApKernelLog].Caught.Should().Be(2);
    }

    [Fact]
    public void Aligner_ErrorsAfterDropAttach_AndRebootExplains()
    {
        var t = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var events = new List<LinkEvent>
        {
            new(t, t.AddSeconds(1), LinkEventType.LinkDown, LinkSource.ApKernelLog, LinkSide.Downstream),
            new(t.AddSeconds(3), t.AddSeconds(4), LinkEventType.LinkUp, LinkSource.ApKernelLog, LinkSide.Downstream, SpeedMbps: 1000),
            new(t.AddSeconds(5), t.AddSeconds(30), LinkEventType.ErrorBurst, LinkSource.ApCrcCounter, LinkSide.Downstream, Count: 2),
        };
        var incidents = LinkEventAligner.Align(events, [], [], new HashSet<string> { ApMac });
        incidents.Should().ContainSingle();
        incidents[0].Kind.Should().Be(IncidentKind.Drop);
        incidents[0].AttachedErrors.Should().Be(2);

        var explained = LinkEventAligner.Align(events, [new ExplainWindow(t.AddMinutes(-1), t.AddMinutes(2), ApMac, ExplainReason.DeviceRebooted)],
            [], new HashSet<string> { ApMac });
        explained[0].Explained.Should().Be(ExplainReason.DeviceRebooted);
    }

    [Fact]
    public void AccessPort_IdleHour_DropsAreSleep()
    {
        var link = new LinkRecord
        {
            Id = $"{SwitchMac}|7-access", Kind = LinkKind.Access,
            Upstream = new LinkEnd { DeviceMac = SwitchMac, DeviceName = "Switch A", DeviceType = DeviceType.Switch, PortIdx = 7 },
            Downstream = new LinkEnd { DeviceMac = "", DeviceName = "PC", IsUniFi = false },
        };
        var hour = new DateTime(2026, 1, 1, 3, 0, 0, DateTimeKind.Utc);
        var ports = new[]
        {
            new PortFieldStats(hour, SwitchMac, "Port 7", "pt:7", "link_down_count", 50, 52, 50, 52, null),
            new PortFieldStats(hour, SwitchMac, "Port 7", "7", "rate_in_bps", 0, 0, 0, 100, 50),
            new PortFieldStats(hour, SwitchMac, "Port 7", "7", "rate_out_bps", 0, 0, 0, 100, 50),
        };
        var built = LanHealthEvidenceBuilder.BuildHour(link, new LanHealthEvidenceBuilder.HourInputs { Hour = hour, Ports = ports },
            new Dictionary<string, LinkRecord>(), new Dictionary<string, HashSet<string>>(), 1);
        built.Incidents.Should().OnlyContain(i => i.Explained == ExplainReason.DeviceAsleep);
        built.Row.DropsExplained.Should().Be(2);
    }

    [Theory]
    [InlineData(1048623, 1000)]
    [InlineData(1048687, 2500)]
    [InlineData(1049064, 10000)]
    [InlineData(1057056, 25000)]
    public void SpeedCaps_DecodeTheFastestSpeed(long caps, int max) => PortSpeedCaps.MaxMbps(caps).Should().Be(max);

    [Fact]
    public void SpeedResolver_FollowsTheTrustRules()
    {
        LinkSpeedResolver.Negotiated(isGatewayPort: true, consoleMbps: 2500, snmpBps: 10_000_000_000).Should().Be(2500);
        LinkSpeedResolver.Negotiated(isGatewayPort: true, consoleMbps: null, snmpBps: 10_000_000_000).Should().BeNull();
        LinkSpeedResolver.Negotiated(isGatewayPort: false, consoleMbps: 1000, snmpBps: 100_000_000).Should().Be(100);
        LinkSpeedResolver.Negotiated(isGatewayPort: true, consoleMbps: 0, snmpBps: 1_000_000_000, isWan: true).Should().Be(1000);
        LinkSpeedResolver.WiredLink(2500, 1000, downstreamIsGateway: false).Should().Be(1000);
        LinkSpeedResolver.WiredLink(2500, 1000, downstreamIsGateway: true).Should().Be(2500);
    }

    [Theory]
    [InlineData("br0", true, false)]
    [InlineData("wgsrv1", true, false)]
    [InlineData("gre1", true, false)]
    [InlineData("ifbeth4", true, false)]
    [InlineData("eth2.100", true, false)]
    [InlineData("ppp0", true, false)]
    [InlineData("eth2", true, true)]
    public void OnlyRealLanPortsScore(string ifName, bool inPortTable, bool scored)
    {
        // Bridges, VPN, GRE, IFB, PPPoE and Wi-Fi never appear in a port table; a VLAN sub-interface is refused by name.
        var port = inPortTable && !ifName.StartsWith("eth2.", StringComparison.Ordinal) && ifName.StartsWith("eth", StringComparison.Ordinal)
            ? new SwitchPort { PortIdx = 3, IfName = ifName } : null;
        if (ifName == "eth2.100") port = new SwitchPort { PortIdx = 3, IfName = "eth2" };
        LanHealthPorts.IsScored(port, ifName, isGateway: true).Should().Be(scored);
    }

    [Fact]
    public void WanPort_IsNotScored() =>
        LanHealthPorts.IsScored(new SwitchPort { PortIdx = 5, IfName = "eth4", IsUplink = true }, "eth4", isGateway: true).Should().BeFalse();

    [Fact]
    public void PortTableFields_TolerateOddTypes()
    {
        const string json = """
            {"port_idx": 3, "link_down_count": "15", "rx_errors": "", "stp_state": 4, "stp_role": true,
             "poe_class": 4, "poe_current": 61.0, "speed_caps": "1048623", "stp_state_change_count": {"change_count": 1},
             "lacp_state": "none", "last_connection": {"mac": 123, "connected_at": "1766591870"}}
            """;
        var port = System.Text.Json.JsonSerializer.Deserialize<SwitchPort>(json)!;
        port.LinkDownCount.Should().Be(15);
        port.RxErrors.Should().BeNull("an empty string is not reported, never 0");
        port.StpState.Should().Be("4");
        port.PoeClass.Should().Be("4");
        port.StpStateChangeCount.Should().BeNull();
        port.LacpState.Should().BeNull();
        port.LastConnection!.ConnectedAt.Should().Be(1766591870);
        PortSpeedCaps.MaxMbps(port.SpeedCaps).Should().Be(1000);
    }

    [Fact]
    public void JumboMismatch_OnlyWhereBothDevicesHaveTheSetting()
    {
        LinkHealth Health(bool downHasSetting) => new()
        {
            Link = new LinkRecord
            {
                Id = "l", Kind = LinkKind.FabricWired,
                Upstream = new LinkEnd { DeviceMac = SwitchMac, DeviceName = "Switch A", DeviceType = DeviceType.Switch, PortIdx = 5, HasJumboSetting = true },
                Downstream = new LinkEnd { DeviceMac = ApMac, DeviceName = "Device B", DeviceType = DeviceType.AccessPoint, PortIdx = 1, HasJumboSetting = downHasSetting },
                UpstreamPort = new PortConfig { Jumbo = true },
                DownstreamPort = new PortConfig { Jumbo = false },
            },
        };
        // Today's APs report no jumboframe_enabled; one that gains the setting is compared like a switch.
        LanSiteDetectors.MtuOnPath(new SiteEvidence { Links = [Health(false)] }).Should().BeEmpty();
        LanSiteDetectors.MtuOnPath(new SiteEvidence { Links = [Health(true)] }).Should().ContainSingle();

        // Config Optimizer's "disabled on this device" result becomes the link finding's cause and fix.
        var cause = new NetworkOptimizer.Diagnostics.Models.PerformanceIssue
        {
            Title = "Jumbo Frames Disabled on Device B", Description = "Device B is off the global setting.",
            Recommendation = "Turn it on for Device B.", DeviceName = "Device B",
        };
        var f = LanSiteDetectors.MtuOnPath(new SiteEvidence { Links = [Health(true)] },
            new Dictionary<string, NetworkOptimizer.Diagnostics.Models.PerformanceIssue> { ["Device B"] = cause }).Single();
        f.Body.Should().EndWith("Device B is off the global setting. Turn it on for Device B.");
    }

    [Fact]
    public void UnusualUtilization_IgnoresCounterSpikes_NeedsSustainedMean()
    {
        var link = ApLink();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var baseline = Enumerable.Range(0, 48).Select(i => new LinkHourSample(start.AddHours(i), 1e8, 1e8, 5e7, 5e7, null, null, null, null, null)).ToList();
        LinkEvidence Ev(LinkHourSample hour) => new() { Link = link, From = start.AddDays(3), To = start.AddDays(3).AddHours(1), Hours = [hour], BaselineHours = baseline };
        var t = start.AddDays(3);
        // A counter jump: one sample above the link's speed in an otherwise quiet hour.
        LanHealthDetectors.UnusualUtilization(Ev(new LinkHourSample(t, 2.9e9, 1e8, 6e7, 5e7, null, null, null, null, null)), FindingText.Utc).Should().BeNull();
        // A busy peak with a quiet mean is not sustained load.
        LanHealthDetectors.UnusualUtilization(Ev(new LinkHourSample(t, 2.4e9, 1e8, 3e8, 5e7, null, null, null, null, null)), FindingText.Utc).Should().BeNull();
        // Sustained: the hour's mean is over half the link and far above its normal.
        LanHealthDetectors.UnusualUtilization(Ev(new LinkHourSample(t, 2.4e9, 1e8, 1.6e9, 5e7, null, null, null, null, null)), FindingText.Utc).Should().NotBeNull();
    }

    [Fact]
    public void Discards_ReportedOnlyAboveTheCalibratedNormalAndTheLinksBaseline()
    {
        var link = ApLink(); // gateway port egressing to an AP: up to ~515 per million is normal
        LinkEvidence Ev(double discards, double baselineRate) => new()
        {
            Link = link,
            Upstream = new EndCounters { TxDiscards = discards, TxPackets = 100_000_000 },
            BaselineHours = [new LinkHourSample(DateTime.UtcNow.AddDays(-2), null, null, null, null, null, null, null, baselineRate * 100, null, 100_000_000)],
        };
        LanHealthDetectors.Discards(Ev(51_500, 400), []).Should().BeNull("515 per million is normal into an AP");
        LanHealthDetectors.Discards(Ev(200_000, 1_500), []).Should().BeNull("2000 per million is not well above this link's own 1500");
        LanHealthDetectors.Discards(Ev(200_000, 300), []).Should().NotBeNull();
    }

    [Fact]
    public void ChangeDetail_DescribesActorObjectAndFields()
    {
        ChangeDetail.Describe(ChangeDetail.Format("Admin", "Port Profile AP Uplink: Link Speed, Flow Control, and 5 more"))
            .Should().Be("Change by Admin: Port Profile AP Uplink (Link Speed, Flow Control, and 5 more)");
        ChangeDetail.Describe(ChangeDetail.Format(null, "Device Switch A")).Should().Be("Change: Device Switch A");
    }

    [Fact]
    public void PortSwapStep_NamesTheDeviceThatMoves()
    {
        var link = new LinkRecord
        {
            Id = "l", Kind = LinkKind.FabricWired, SwapNeighbor = "AP 2",
            Upstream = new LinkEnd { DeviceMac = SwitchMac, DeviceName = "Gateway A", DeviceType = DeviceType.Gateway, PortIdx = 3 },
            Downstream = new LinkEnd { DeviceMac = ApMac, DeviceName = "AP 1", DeviceType = DeviceType.AccessPoint, PortIdx = 1 },
        };
        LanHealthDetectors.PortSwapStep(link, 3).Should().Be(
            "Swap the cables in Port 3 and Port 2 on Gateway A (AP 2 moves to Port 3). If the drops stay on Port 3, it's the gateway port. If they follow AP 1, it's the AP or its cable.");
    }
    [Fact]
    public void Scorer_CombinesFindingsAndWeighsFabricByClients()
    {
        LanHealthScorer.LinkScore([new LinkFinding { Points = 20 }, new LinkFinding { Points = 20 }, new LinkFinding { Points = 0 }]).Should().Be(64);
        var fabric = new LinkHealth { Link = ApLink(), Score = 50 };
        var access = new LinkHealth
        {
            Link = new LinkRecord { Id = "x", Kind = LinkKind.Access, ClientsBehind = 1, Upstream = new LinkEnd { DeviceType = DeviceType.Switch } },
            Score = 0, Exclusion = ExclusionLevel.ExcludeFromScore,
        };
        LanHealthScorer.SiteScore([fabric, access]).Should().BeLessThan(100).And.BeGreaterThan(0);
        LanHealthReportBuilder.ExclusionFor(ApLink(), [new NetworkOptimizer.Storage.Models.LanHealthExclusion { Scope = "device", Key = ApMac, Level = 1 }], false)
            .Should().Be(ExclusionLevel.MuteAlerts, "a fabric link can be muted, never excluded");
    }
}
