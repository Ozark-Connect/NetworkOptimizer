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
        Probes = new Dictionary<string, (int, double, double?)> { [Target] = (720, 0.04, 0.4) },
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
    public void Aligner_DownThenUpMinutesLater_IsOneOutageWithItsDowntime()
    {
        var t = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var events = new List<LinkEvent>
        {
            new(t, t.AddSeconds(1), LinkEventType.LinkDown, LinkSource.ApKernelLog, LinkSide.Downstream),
            new(t.AddSeconds(150), t.AddSeconds(151), LinkEventType.LinkUp, LinkSource.ApKernelLog, LinkSide.Downstream, SpeedMbps: 1000),
        };

        var incidents = LinkEventAligner.Align(events, [], [], new HashSet<string> { ApMac });

        incidents.Should().ContainSingle();
        incidents[0].Kind.Should().Be(IncidentKind.Outage);
        incidents[0].DownSeconds.Should().BeApproximately(150, 1);
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

    /// <summary>
    /// <paramref name="relinks"/> relinks in an hour, seen by link_down_count, ifLastChange and ifOperStatus; the port was down for
    /// <paramref name="downShare"/> of it.
    /// </summary>
    private static LanHealthEvidenceBuilder.LinkHour OneRelinkHour(double downShare, int relinks = 1, bool alwaysOn = false)
    {
        var link = new LinkRecord
        {
            Id = $"{SwitchMac}|7-access", Kind = LinkKind.Access, AlwaysOnClient = alwaysOn,
            Upstream = new LinkEnd { DeviceMac = SwitchMac, DeviceName = "Switch A", DeviceType = DeviceType.Switch, PortIdx = 7 },
            Downstream = new LinkEnd { DeviceMac = "", DeviceName = "PC", IsUniFi = false },
        };
        var hour = new DateTime(2026, 1, 1, 7, 0, 0, DateTimeKind.Utc);
        var ports = new[]
        {
            new PortFieldStats(hour, SwitchMac, "Port 7", "pt:7", "link_down_count", 127, 127 + relinks, 127, 127 + relinks, null),
            new PortFieldStats(hour, SwitchMac, "Port 7", "0/7", "last_change", 215_993_300, 216_505_900, 215_993_300, 216_505_900, null),
            new PortFieldStats(hour, SwitchMac, "Port 7", "0/7", "oper_status", 1, 1, 1, 2, 1 + downShare),
            // Busy hours either side of the sleep: the idle-traffic check does not apply.
            new PortFieldStats(hour, SwitchMac, "Port 7", "0/7", "rate_in_bps", 0, 0, 0, 5_000_000, 18_000),
            new PortFieldStats(hour, SwitchMac, "Port 7", "0/7", "rate_out_bps", 0, 0, 0, 50_000_000, 540_000),
        };
        return LanHealthEvidenceBuilder.BuildHour(link, new LanHealthEvidenceBuilder.HourInputs { Hour = hour, Ports = ports },
            new Dictionary<string, LinkRecord>(), new Dictionary<string, HashSet<string>>(), 1);
    }

    [Fact]
    public void AccessPort_ThreeCountersOneRelink_IsOneIncident()
    {
        // Down for about 10 s: a real relink, not sleep, and counted once however many counters saw it.
        var built = OneRelinkHour(10.0 / 3600);
        built.Incidents.Should().ContainSingle(i => i.Kind == IncidentKind.Relink && i.Count == 1 && i.Explained == null);
        built.Incidents.Single().SeenBy.Should().BeEquivalentTo([LinkSource.SwitchLinkDownCount, LinkSource.SnmpLastChange, LinkSource.SnmpOperStatus]);
        built.Row.Relinks.Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Aligner_CountersThatDisagree_CountTheMostAnyOneSaw(bool biggerFirst)
    {
        var from = new DateTime(2026, 1, 1, 7, 0, 0, DateTimeKind.Utc);
        var downCount = new LinkEvent(from, from.AddHours(1), LinkEventType.Relink, LinkSource.SwitchLinkDownCount, LinkSide.Upstream, Count: 3);
        var lastChange = new LinkEvent(from, from.AddHours(1), LinkEventType.Relink, LinkSource.SnmpLastChange, LinkSide.Upstream);
        var incidents = LinkEventAligner.Align(biggerFirst ? [downCount, lastChange] : [lastChange, downCount], [], [], new HashSet<string> { SwitchMac });
        incidents.Where(i => i.Kind == IncidentKind.Relink).Sum(i => i.Count).Should().Be(3);
    }

    [Fact]
    public void Aligner_PreciseDropAndCounters_AreOneDrop()
    {
        var t = new DateTime(2026, 1, 1, 7, 20, 0, DateTimeKind.Utc);
        var hour = new DateTime(2026, 1, 1, 7, 0, 0, DateTimeKind.Utc);
        var events = new List<LinkEvent>
        {
            new(t, t.AddSeconds(1), LinkEventType.LinkDown, LinkSource.ApKernelLog, LinkSide.Downstream),
            new(t.AddSeconds(3), t.AddSeconds(4), LinkEventType.LinkUp, LinkSource.ApKernelLog, LinkSide.Downstream, SpeedMbps: 1000),
            new(hour, hour.AddHours(1), LinkEventType.Relink, LinkSource.SwitchLinkDownCount, LinkSide.Upstream),
            new(hour, hour.AddHours(1), LinkEventType.Relink, LinkSource.SnmpLastChange, LinkSide.Upstream),
        };
        var incidents = LinkEventAligner.Align(events, [], [], new HashSet<string> { ApMac });
        incidents.Should().ContainSingle().Which.Kind.Should().Be(IncidentKind.Drop);
    }

    [Fact]
    public void AccessPort_DownForMinutesInsideTheHour_IsSleep()
    {
        // A PC asleep 16 minutes, down and back up inside one hour.
        var built = OneRelinkHour(16.0 / 60);
        built.Incidents.Should().ContainSingle().Which.Explained.Should().Be(ExplainReason.DeviceAsleep);
        built.Row.Relinks.Should().Be(0);
        built.Row.DropsExplained.Should().Be(1);
    }

    [Fact]
    public void AlwaysOnClient_DownForMinutes_IsNotSleep()
    {
        // A server down 16 minutes is a fault, not a nap.
        var built = OneRelinkHour(16.0 / 60, alwaysOn: true);
        built.Incidents.Should().ContainSingle(i => i.Kind == IncidentKind.Relink && i.Explained == null);
        built.Row.Relinks.Should().Be(1);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void AlwaysOnClient_IdleOrTenHundredRelink_IsNotSleep(bool alwaysOn, bool explained)
    {
        var link = new LinkRecord
        {
            Id = $"{SwitchMac}|7-access", Kind = LinkKind.Access, AlwaysOnClient = alwaysOn,
            Upstream = new LinkEnd { DeviceMac = SwitchMac, DeviceName = "Switch A", DeviceType = DeviceType.Switch, PortIdx = 7 },
            Downstream = new LinkEnd { DeviceMac = "", DeviceName = "NAS", IsUniFi = false },
        };
        var hour = new DateTime(2026, 1, 1, 3, 0, 0, DateTimeKind.Utc);
        var idle = new[]
        {
            new PortFieldStats(hour, SwitchMac, "Port 7", "pt:7", "link_down_count", 50, 52, 50, 52, null),
            new PortFieldStats(hour, SwitchMac, "Port 7", "7", "rate_in_bps", 0, 0, 0, 100, 50),
            new PortFieldStats(hour, SwitchMac, "Port 7", "7", "rate_out_bps", 0, 0, 0, 100, 50),
        };
        var downshift = new[]
        {
            new PortFieldStats(hour, SwitchMac, "Port 7", "pt:7", "link_down_count", 50, 51, 50, 51, null),
            new PortFieldStats(hour, SwitchMac, "Port 7", "pt:7", NetworkOptimizer.Storage.Services.InfluxFieldNames.LinkSpeedMbps, 1000, 1000, 100, 1000, null),
            new PortFieldStats(hour, SwitchMac, "Port 7", "7", "rate_in_bps", 0, 0, 0, 50_000_000, 5_000_000),
            new PortFieldStats(hour, SwitchMac, "Port 7", "7", "rate_out_bps", 0, 0, 0, 50_000_000, 5_000_000),
        };
        foreach (var ports in new[] { idle, downshift })
        {
            var built = LanHealthEvidenceBuilder.BuildHour(link, new LanHealthEvidenceBuilder.HourInputs { Hour = hour, Ports = ports },
                new Dictionary<string, LinkRecord>(), new Dictionary<string, HashSet<string>>(), 1);
            built.Incidents.Where(i => i.Kind is IncidentKind.Relink or IncidentKind.Drop)
                .Should().NotBeEmpty().And.OnlyContain(i => (i.Explained == ExplainReason.DeviceAsleep) == explained);
        }
    }

    [Fact]
    public void NeverSleeps_CoversServersCamerasAndHubs_NotPcs()
    {
        LanHealthEvidenceBuilder.NeverSleeps(NetworkOptimizer.Core.Enums.ClientDeviceCategory.Server).Should().BeTrue();
        LanHealthEvidenceBuilder.NeverSleeps(NetworkOptimizer.Core.Enums.ClientDeviceCategory.NAS).Should().BeTrue();
        LanHealthEvidenceBuilder.NeverSleeps(NetworkOptimizer.Core.Enums.ClientDeviceCategory.Camera).Should().BeTrue();
        LanHealthEvidenceBuilder.NeverSleeps(NetworkOptimizer.Core.Enums.ClientDeviceCategory.Unknown).Should().BeFalse();
    }

    [Theory]
    [InlineData("Ubiquiti Inc", true)]
    [InlineData("Ubiquiti Networks Inc.", true)]
    [InlineData("Intel Corporate", false)]
    [InlineData(null, false)]
    public void UbiquitiOui_IsAlwaysOn(string? oui, bool expected) => LanHealthEvidenceBuilder.IsUbiquitiOui(oui).Should().Be(expected);

    [Fact]
    public void AccessPort_ManyShortFlapsAddingUpToMinutes_AreNotSleep()
    {
        // Ten flaps of 15 s: 150 s down in the hour, but seconds each.
        var built = OneRelinkHour(150.0 / 3600, relinks: 10);
        built.Incidents.Should().NotContain(i => i.Explained == ExplainReason.DeviceAsleep);
        built.Row.Relinks.Should().Be(10);
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
    public void TroubleSince_ReachesBackUntilAThreeDayGap()
    {
        NetworkOptimizer.Storage.Models.LanHealthLinkHour Hour(DateTime h, int drops) => new() { LinkId = "l", HourStart = h, Drops = drops };
        var now = new DateTime(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc);
        var window = new[] { Hour(now.AddHours(-10), 2) };
        var baseline = new[]
        {
            Hour(now.AddDays(-12), 1),            // more than 3 days before the next: a separate run
            Hour(now.AddDays(-7), 1),
            Hour(now.AddDays(-5), 1),
            Hour(now.AddDays(-3), 0),
            Hour(now.AddDays(-2), 1),
        };
        LanHealthReportBuilder.TroubleSince(window, baseline).Should().Be(now.AddDays(-7));
        LanHealthReportBuilder.CleanDaysBefore(window, baseline).Should().Be(5);
    }
    [Fact]
    public void Detectors_SkipWiredChecksOnMeshLinks()
    {
        var link = new LinkRecord
        {
            Id = "m", Kind = LinkKind.FabricMeshMlo, SpeedMbps = 6200,
            Upstream = new LinkEnd { DeviceMac = SwitchMac, DeviceName = "AP 2", DeviceType = DeviceType.AccessPoint },
            Downstream = new LinkEnd { DeviceMac = ApMac, DeviceName = "AP 1", DeviceType = DeviceType.AccessPoint },
        };
        var ev = new LinkEvidence { Link = link, UsualSpeedMbps = 6700, From = DateTime.UtcNow.AddHours(-48), To = DateTime.UtcNow };
        LanHealthDetectors.Run(ev, [], FindingText.Utc).Should().NotContain(f => f.Detector == Detectors.SpeedDownshift);
    }

    [Fact]
    public void HiddenSwitch_SkipsAServerPort()
    {
        var link = new LinkRecord
        {
            Id = "a", Kind = LinkKind.Access, ServerOnPort = true,
            Upstream = new LinkEnd { DeviceMac = SwitchMac, DeviceName = "Switch A", DeviceType = DeviceType.Switch, PortIdx = 10 },
            Downstream = new LinkEnd { DeviceName = "Hypervisor", IsUniFi = false },
            UpstreamPort = new PortConfig { MacTableCount = 7 },
        };
        LanHealthDetectors.HiddenSwitch(new LinkEvidence { Link = link }).Should().BeNull();
    }
    [Fact]
    public void AccessDegraded_IgnoresBusyHoursAtFullSpeed()
    {
        var link = new LinkRecord
        {
            Id = "a", Kind = LinkKind.Access, SpeedMbps = 10,
            Upstream = new LinkEnd { DeviceMac = SwitchMac, DeviceName = "Gateway A", DeviceType = DeviceType.Gateway, PortIdx = 2 },
            Downstream = new LinkEnd { DeviceName = "PC", IsUniFi = false },
        };
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        LinkHourSample Hour(int h, double bps) => new(t0.AddHours(h), bps, bps, bps, bps, null, null, null, null, null);
        // Busy while awake at 2.5G, idle once it dropped to 10M (asleep): no finding.
        var sleeping = new LinkEvidence
        {
            Link = link, UsualSpeedMbps = 2500,
            Hours = [Hour(0, 5e7), Hour(1, 5e7), Hour(2, 1e3), Hour(3, 1e3)],
            Speeds = [new SpeedReading(t0, 2500, true, LinkSource.PortTableState), new SpeedReading(t0.AddHours(2), 10, true, LinkSource.PortTableState)],
        };
        LanHealthDetectors.AccessDegraded(sleeping).Should().BeNull();
        // Busy at 10M for two hours: degraded.
        var degraded = new LinkEvidence
        {
            Link = link, UsualSpeedMbps = 2500,
            Hours = [Hour(0, 5e7), Hour(2, 5e6), Hour(3, 5e6)],
            Speeds = [new SpeedReading(t0, 2500, true, LinkSource.PortTableState), new SpeedReading(t0.AddHours(2), 10, true, LinkSource.PortTableState)],
        };
        LanHealthDetectors.AccessDegraded(degraded).Should().NotBeNull();
    }
    [Fact]
    public void LinkDrops_WeighsALongRunMore_AndSaysWhenDropsOutliveAChange()
    {
        var link = ApLink();
        var now = new DateTime(2026, 1, 22, 0, 0, 0, DateTimeKind.Utc);
        List<LinkIncident> Drops() => Enumerable.Range(0, 10).Select(i =>
        {
            var t = now.AddHours(-40 + i * 4);
            return new LinkIncident { Start = t, End = t.AddSeconds(3), Kind = IncidentKind.Drop, SpeedAfterMbps = 2500 };
        }).ToList();
        LinkEvidence Ev(DateTime? since) => new() { Link = link, From = now.AddHours(-48), To = now, TroubleSince = since };

        var burst = LanHealthDetectors.LinkDrops(Ev(now.AddHours(-40)), Drops(), FindingText.Utc)!;
        var chronic = LanHealthDetectors.LinkDrops(Ev(now.AddDays(-14)), Drops(), FindingText.Utc)!;
        chronic.Points.Should().BeApproximately(Math.Min(90, burst.Points * 1.5), 0.01);

        var withChange = Drops();
        withChange.Add(new LinkIncident { Start = now.AddHours(-30), End = now.AddHours(-30), Kind = IncidentKind.Relink, Explained = ExplainReason.PortProfileChanged });
        LanHealthDetectors.LinkDrops(Ev(null), withChange, FindingText.Utc)!.Body.Should().Contain("Still dropping after the");
    }
    [Fact]
    public void Bottleneck_ComparesUsableCapacityWithTheSpeedTestOverheads()
    {
        LinkHealth Wired(string up, string down, int mbps) => new() { Link = new LinkRecord
        {
            Id = $"{up}-{down}", Kind = LinkKind.FabricWired, SpeedMbps = mbps,
            Upstream = new LinkEnd { DeviceMac = up, DeviceName = up, DeviceType = DeviceType.Gateway, PortIdx = 3 },
            Downstream = new LinkEnd { DeviceMac = down, DeviceName = down, DeviceType = DeviceType.AccessPoint },
        } };
        LinkHealth Mesh(string up, string down, int mbps) => new() { Link = new LinkRecord
        {
            Id = $"{up}-{down}", Kind = LinkKind.FabricMeshMlo, SpeedMbps = mbps,
            Upstream = new LinkEnd { DeviceMac = up, DeviceName = up, DeviceType = DeviceType.AccessPoint },
            Downstream = new LinkEnd { DeviceMac = down, DeviceName = down, DeviceType = DeviceType.AccessPoint },
        } };
        // 6.1 Gbps PHY is about 3.4 Gbps usable over mesh: more than a 2.5G uplink's 2.35.
        LanSiteDetectors.Bottleneck(new SiteEvidence { Links = [Wired("gw", "ap1", 2500), Mesh("ap1", "ap2", 6100)] }).Should().ContainSingle();
        // 4 Gbps PHY is about 2.2 Gbps usable: not a bottleneck behind 2.5G.
        LanSiteDetectors.Bottleneck(new SiteEvidence { Links = [Wired("gw", "ap1", 2500), Mesh("ap1", "ap2", 4000)] }).Should().BeEmpty();
    }
    [Fact]
    public void ChipCounters_NeverWriteTheConsoleLinkFields()
    {
        var fields = SwitchPortLinkSink.CounterFields(new NetworkOptimizer.AgentProtocol.SwitchPortCounters { IfName = "eth0", Up = true, SpeedMbps = 10000 })
            .Select(f => f.Key).ToList();
        fields.Should().Contain([NetworkOptimizer.Storage.Services.InfluxFieldNames.ChipLinkSpeedMbps, NetworkOptimizer.Storage.Services.InfluxFieldNames.ChipLinkUp]);
        fields.Should().NotContain([NetworkOptimizer.Storage.Services.InfluxFieldNames.LinkSpeedMbps, NetworkOptimizer.Storage.Services.InfluxFieldNames.LinkUp]);
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

    // AP uplink with 5 of 20 clients: share 0.25.
    private static List<LinkRecord> QuarterSite() =>
        [ApLink(), new LinkRecord { Id = "access-1", Kind = LinkKind.Access, ClientsBehind = 15, Upstream = new LinkEnd { DeviceType = DeviceType.Switch } }];

    [Fact]
    public void HourImpairment_ADropCostsItsClientsReconnectTime()
    {
        var link = ApLink();
        var rows = new List<NetworkOptimizer.Storage.Models.LanHealthLinkHour> { new() { LinkId = link.Id, Drops = 1, DownSeconds = 3 } };
        LanHealthReportBuilder.HourImpairment(rows, QuarterSite(), [], false).Impairment
            .Should().BeApproximately(0.25 * 60 / 3600, 1e-9, "an AP's clients reassociate, so a 3 s drop costs a minute");
    }

    [Fact]
    public void HourImpairment_AnOpenFlappingFindingCostsEveryHour()
    {
        var link = ApLink();
        var open = new List<LinkFinding> { new() { Detector = Detectors.LinkDrops, LinkId = link.Id, Severity = FindingSeverity.Warning } };
        var (impairment, worst) = LanHealthReportBuilder.HourImpairment([], QuarterSite(), [], false, open);
        impairment.Should().BeApproximately(0.25 * 0.15, 1e-9, "the link is unstable between drops too");
        worst.Should().Be(link.Name);
    }

    [Fact]
    public void HourImpairment_OnlyUnstableFindingsCarryACondition()
    {
        var link = ApLink();
        var open = new List<LinkFinding> { new() { Detector = Detectors.SpeedDownshift, LinkId = link.Id, Severity = FindingSeverity.Warning } };
        LanHealthReportBuilder.HourImpairment([], QuarterSite(), [], false, open).Impairment.Should().Be(0);
    }

    [Fact]
    public void LiveHours_IncludeTheLastClosedHourUntilItIsStored()
    {
        var now = new DateTime(2026, 11, 23, 19, 9, 0, DateTimeKind.Utc);
        LanHealthEngine.LiveHoursFor(now, previousStored: false).Should().Equal(now.Date.AddHours(18), now.Date.AddHours(19));
        LanHealthEngine.LiveHoursFor(now, previousStored: true).Should().Equal(now.Date.AddHours(19));
    }
}