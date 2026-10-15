using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// The WAN link speed alert, driven one console snapshot at a time with time advanced between
/// them, the way the medium tier drives it.
/// </summary>
public class WanLinkSpeedAlertEvaluatorTests
{
    private const string Mac = "aa:bb:cc:dd:ee:ff";
    private static readonly DateTime Start = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly CapturingBus _bus = new();
    private readonly WanLinkSpeedAlertEvaluator _evaluator;
    private DateTime _now = Start;

    public WanLinkSpeedAlertEvaluatorTests()
    {
        _evaluator = new WanLinkSpeedAlertEvaluator(_bus, NullLogger<WanLinkSpeedAlertEvaluator>.Instance);
    }

    private static WanLinkSpeedObservation Port(int? speed, int? expected = 1000, bool? fullDuplex = true, bool autoneg = true) =>
        new(Mac, "Gateway", "eth3", "Port 4", "Fiber ISP", speed, fullDuplex, autoneg, expected);

    /// <summary>Feeds the same reading once a minute for <paramref name="minutes"/> minutes.</summary>
    private async Task Hold(WanLinkSpeedObservation o, int minutes)
    {
        for (var i = 0; i < minutes; i++)
        {
            await _evaluator.EvaluateAsync(o, _now);
            _now = _now.AddMinutes(1);
        }
    }

    private IEnumerable<AlertEvent> Degraded => _bus.Published.Where(e => e.EventType == WanLinkSpeedAlertEvaluator.DegradedEventType);
    private IEnumerable<AlertEvent> Recovered => _bus.Published.Where(e => e.EventType == WanLinkSpeedAlertEvaluator.RecoveredEventType);

    [Fact]
    public async Task HealthyLink_PublishesNothing()
    {
        await Hold(Port(1000), 30);

        _bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task SustainedDownshift_OpensOnceAfterSustain()
    {
        await Hold(Port(1000), 3);
        await Hold(Port(100), 5);
        Degraded.Should().BeEmpty("the drop has not held for the sustain window yet");

        await Hold(Port(100), 20);

        var alert = Degraded.Should().ContainSingle().Subject;
        alert.Source.Should().Be("wan");
        alert.Severity.Should().Be(AlertSeverity.Warning);
        alert.DeviceId.Should().Be($"{Mac}/eth3");
        alert.Title.Should().Contain("Fiber ISP");
        alert.Message.Should().Contain("100 Mbps").And.Contain("1000 Mbps").And.Contain("manually");
    }

    [Fact]
    public async Task BriefDownshift_DuringRenegotiation_StaysSilent()
    {
        await Hold(Port(1000), 3);
        await Hold(Port(100), 2);
        await Hold(Port(1000), 30);

        _bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Recovery_PublishedAfterSustain_OnSamePort()
    {
        await Hold(Port(100), 10);
        await Hold(Port(1000), 10);

        Degraded.Should().ContainSingle();
        var recovered = Recovered.Should().ContainSingle().Subject;
        recovered.DeviceId.Should().Be($"{Mac}/eth3");
        recovered.Severity.Should().Be(AlertSeverity.Info);
    }

    [Fact]
    public async Task HalfDuplex_AtFullSpeed_IsDegraded()
    {
        await Hold(Port(1000, fullDuplex: false), 10);

        Degraded.Should().ContainSingle().Which.Message.Should().Contain("half duplex");
    }

    [Fact]
    public async Task FurtherDrop_WhileOpen_RepublishesOnce()
    {
        await Hold(Port(100), 10);
        await Hold(Port(10), 10);

        Degraded.Should().HaveCount(2);
        Degraded.Last().Message.Should().Contain("10 Mbps");
    }

    [Fact]
    public async Task UnknownExpected_OrPortDown_SaysNothing()
    {
        await Hold(Port(100, expected: null), 15);
        await Hold(Port(null), 15);

        _bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task GapBetweenSnapshots_RestartsTheSustainRun()
    {
        await _evaluator.EvaluateAsync(Port(100), _now);
        // Console unreachable for an hour: the two readings either side are not 60 minutes held.
        _now = _now.AddMinutes(60);
        await _evaluator.EvaluateAsync(Port(100), _now);

        Degraded.Should().BeEmpty();
    }

    [Fact]
    public async Task ExpectedFromPlan_DoesNotClaimThePortReachedIt()
    {
        await Hold(Port(100) with { ExpectedFromPlan = true }, 10);

        Degraded.Should().ContainSingle().Which.Message.Should()
            .Contain("internet plan needs").And.NotContain("normally reaches");
    }

    [Fact]
    public async Task ForcedSpeedPort_AdvisesTheCable()
    {
        await Hold(Port(100, autoneg: false), 10);

        Degraded.Should().ContainSingle().Which.Message.Should().Contain("already set manually");
    }

    [Fact]
    public async Task DefaultSite_HasNoSiteSuffix_OtherSitesDo()
    {
        var bus = new CapturingBus();
        var other = new WanLinkSpeedAlertEvaluator(bus, NullLogger<WanLinkSpeedAlertEvaluator>.Instance, "branch");
        for (var i = 0; i < 10; i++)
            await other.EvaluateAsync(Port(100), Start.AddMinutes(i));

        bus.Published.Should().ContainSingle().Which.Title.Should().EndWith("(site branch)");
    }

    private sealed class CapturingBus : IAlertEventBus
    {
        public List<AlertEvent> Published { get; } = new();

        public ValueTask PublishAsync(AlertEvent alertEvent, CancellationToken ct = default)
        {
            Published.Add(alertEvent);
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<AlertEvent> ConsumeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
