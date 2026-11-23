using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Web.Services.Monitoring.LanHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services.Monitoring.LanHealth;

/// <summary>LAN Health alerts: sustained both ways, one per loop, quiet for excluded or rollout links, recoveries paired.</summary>
public class LanHealthAlertTests
{
    private static readonly DateTime T0 = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    private sealed class CapturingBus : IAlertEventBus
    {
        public List<AlertEvent> Published { get; } = [];

        public ValueTask PublishAsync(AlertEvent alertEvent, CancellationToken ct = default)
        {
            Published.Add(alertEvent);
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<AlertEvent> ConsumeAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private static LinkHealth Link(string id, bool fabric = true, ExclusionLevel? exclusion = null, params string[] classes)
    {
        var l = new LinkHealth
        {
            Link = new LinkRecord
            {
                Id = id, Kind = fabric ? LinkKind.FabricWired : LinkKind.Access,
                Upstream = new LinkEnd { DeviceMac = "aa:bb:cc:00:00:02", DeviceName = "Switch A", DeviceType = DeviceType.Switch, PortIdx = 3 },
                Downstream = new LinkEnd { DeviceMac = "aa:bb:cc:00:00:03", DeviceName = "AP 1", DeviceType = DeviceType.AccessPoint },
            },
            Exclusion = exclusion,
            Score = 60,
        };
        foreach (var c in classes) l.Findings.Add(new LinkFinding { LinkId = id, Class = c, Title = $"{c}: {l.Link.Name}", Body = "body" });
        return l;
    }

    private static LanHealthReport Report(params LinkHealth[] links) => new() { Links = links.ToList(), Findings = links.SelectMany(l => l.Findings).ToList() };

    private static LanHealthAlertEvaluator Evaluator(CapturingBus bus, string slug = "default") => new(bus, NullLogger.Instance, slug, null);

    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    [Fact]
    public async Task Flapping_OpensOnlyAfterItHoldsFiveMinutes_AndRecoversTheSameWay()
    {
        var bus = new CapturingBus();
        var e = Evaluator(bus);
        await e.EvaluateAsync(Report(Link("l1", classes: "Link Flapping")), None, T0);
        bus.Published.Should().BeEmpty();
        await e.EvaluateAsync(Report(Link("l1", classes: "Link Flapping")), None, T0.AddMinutes(6));
        bus.Published.Should().ContainSingle(a => a.EventType == LanHealthAlertEvaluator.Flapping);
        var opened = bus.Published[0];
        opened.Severity.Should().Be(AlertSeverity.Warning, "a fabric link");
        opened.SourceUrl.Should().Be("/monitoring?tab=lan-health&link=l1");
        opened.Context["link_id"].Should().Be("l1");

        await e.EvaluateAsync(Report(Link("l1")), None, T0.AddMinutes(10));
        await e.EvaluateAsync(Report(Link("l1")), None, T0.AddMinutes(16));
        bus.Published.Should().HaveCount(2);
        bus.Published[1].EventType.Should().Be(LanHealthAlertEvaluator.Stable);
        bus.Published[1].Title.Should().StartWith("Link Stable: ");
    }

    [Fact]
    public async Task AccessLinks_AlertAtInfo()
    {
        var bus = new CapturingBus();
        var e = Evaluator(bus);
        await e.EvaluateAsync(Report(Link("a", fabric: false, classes: "Link Errors")), None, T0);
        await e.EvaluateAsync(Report(Link("a", fabric: false, classes: "Link Errors")), None, T0.AddMinutes(6));
        bus.Published.Single().Severity.Should().Be(AlertSeverity.Info);
    }

    [Fact]
    public async Task SpeedDegraded_AlertsOnAFabricSpeedOrDuplexFault_NotAnAccessClientsSlowerLink()
    {
        LinkHealth WithDetector(string id, bool fabric, int detector)
        {
            var l = Link(id, fabric);
            l.Findings.Add(new LinkFinding { LinkId = id, Class = "Link Speed Degraded", Title = "Link Speed Degraded: x", Body = "b", Detector = detector });
            return l;
        }
        var bus = new CapturingBus();
        var e = Evaluator(bus);
        var report = Report(WithDetector("f", true, Detectors.SpeedDownshift), WithDetector("a", false, Detectors.AccessDegraded));
        await e.EvaluateAsync(report, None, T0);
        await e.EvaluateAsync(report, None, T0.AddMinutes(6));
        bus.Published.Should().ContainSingle().Which.Context["link_id"].Should().Be("f");
    }

    [Fact]
    public async Task ExcludedOrRolloutLinks_StayQuiet()
    {
        var bus = new CapturingBus();
        var e = Evaluator(bus);
        var suppressed = new HashSet<string> { "r" };
        for (var m = 0; m <= 10; m += 5)
            await e.EvaluateAsync(Report(Link("x", exclusion: ExclusionLevel.MuteAlerts, classes: "Link Errors"), Link("r", classes: "Link Errors")), suppressed, T0.AddMinutes(m));
        bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task ALoop_IsOneAlert_NotOnePerStormingPort()
    {
        var bus = new CapturingBus();
        var e = Evaluator(bus);
        var a = Link("a", classes: "Broadcast Storm");
        var b = Link("b", classes: "Broadcast Storm");
        var report = Report(a, b);
        report.Findings.Add(new LinkFinding { LinkId = "a", Class = "Network Loop", Title = "Network Loop: 2 ports", Body = "loop" });
        a.Findings.Add(report.Findings[^1]);
        await e.EvaluateAsync(report, None, T0);
        await e.EvaluateAsync(report, None, T0.AddMinutes(6));
        bus.Published.Should().ContainSingle();
        bus.Published[0].Severity.Should().Be(AlertSeverity.Critical);
        bus.Published[0].Context["link_id"].Should().Be("a");
    }

    [Fact]
    public async Task AManagedSite_NamesItselfInTheTitle()
    {
        var bus = new CapturingBus();
        var e = Evaluator(bus, "branch-2");
        await e.EvaluateAsync(Report(Link("l1", classes: "Unusual Link Utilization")), None, T0);
        await e.EvaluateAsync(Report(Link("l1", classes: "Unusual Link Utilization")), None, T0.AddMinutes(6));
        var a = bus.Published.Single();
        a.Title.Should().EndWith(" (site branch-2)");
        a.Severity.Should().Be(AlertSeverity.Info);
    }

    [Fact]
    public void EveryAlertHasARecoveryPair() =>
        LanHealthAlertEvaluator.Pairs.Should().HaveCount(5).And.ContainKeys(
            LanHealthAlertEvaluator.Flapping, LanHealthAlertEvaluator.SpeedDegraded, LanHealthAlertEvaluator.Errors,
            LanHealthAlertEvaluator.Storm, LanHealthAlertEvaluator.Utilization);

    [Fact]
    public void DeviceStates_OnlyInBetweenStatesExplainDrops()
    {
        NetworkOptimizer.Storage.Services.MonitoringInfluxClient.LanEventPoint State(int min, string state) =>
            new(T0.AddMinutes(min), "aa:bb:cc:00:00:03", LanPortSnapshotRecorder.StateEventType, null, null, state, null);
        var w = LanHealthEngine.StateWindows([State(0, "Provisioning"), State(2, "Online"), State(10, "Offline"), State(20, "Online"), State(30, "Upgrading")], T0.AddHours(1)).ToList();
        w.Should().HaveCount(2, "Offline opens no window");
        w[0].Reason.Should().Be(ExplainReason.DeviceProvisioning);
        w[0].End.Should().Be(T0.AddMinutes(3));
        w[1].Reason.Should().Be(ExplainReason.DeviceUpgrading);
        w[1].End.Should().Be(T0.AddHours(1).AddMinutes(1), "still upgrading at the hour's end");
        LanHealthEngine.StateWindows([State(0, "Provisioning"), State(1, "Offline")], T0.AddHours(1)).Single().End
            .Should().Be(T0.AddMinutes(2), "going offline ends the provisioning stretch");
    }
}