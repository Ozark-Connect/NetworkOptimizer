using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// The PON stick's DDM read artifact must never raise an alert, and a real thermal event must
/// still raise one - one poll later, but never swallowed.
/// </summary>
public class SfpAlertDeferralTests
{
    private const string Mac = "aa:bb:cc:dd:ee:01";
    private static readonly SfpDdmThresholds On = SfpDdmThresholds.Defaults with { IgnoreDdmSpikes = true };
    private static readonly SfpDdmThresholds Off = SfpDdmThresholds.Defaults;

    private sealed class CapturingBus : IAlertEventBus
    {
        public List<AlertEvent> Events { get; } = new();
        public ValueTask PublishAsync(AlertEvent alertEvent, CancellationToken ct = default)
        {
            Events.Add(alertEvent);
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<AlertEvent> ConsumeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private static async Task<CapturingBus> FeedAsync(
        SfpCategory category, SfpDdmThresholds thresholds, params (double Temp, double Rx)[] samples)
    {
        var bus = new CapturingBus();
        var evaluator = new SfpAlertEvaluator(bus, NullLogger<SfpAlertEvaluator>.Instance);
        foreach (var (temp, rx) in samples)
            await evaluator.EvaluateAsync(Mac, "7", "Gateway", category, rx, -1.0, temp, thresholds);
        return bus;
    }

    [Fact]
    public async Task WithTheSettingOffTheArtifactStillAlertsAndPointsAtTheSetting()
    {
        var bus = await FeedAsync(SfpCategory.Pon, Off,
            (42.85, -22.37), (82.0, -18.79), (44.85, -22.29));

        var alert = bus.Events.Should().ContainSingle(e => e.EventType == "monitoring.sfp_temperature").Subject;
        alert.Message.Should().Contain("SFP Stats - SFP Alert Thresholds");
    }

    [Fact]
    public async Task AnOrdinaryAlertCarriesNoSuchPointer()
    {
        var bus = await FeedAsync(SfpCategory.Pon, Off,
            (68.0, -22.37), (71.0, -22.40), (77.0, -22.35));

        bus.Events.Should().OnlyContain(e => !e.Message.Contains("SFP Alert Thresholds"));
    }

    [Fact]
    public async Task TheArtifactRaisesNothing()
    {
        // The live event: 42.85 -> 67.85 -> 44.85 C with RX moving in step and returning.
        var bus = await FeedAsync(SfpCategory.Pon, On,
            (42.85, -22.37), (42.85, -22.44), (67.85, -18.79), (44.85, -22.29), (42.85, -22.44));

        bus.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task ARealThermalEventStillAlertsOnePollLate()
    {
        // Ramps past the PON temperature threshold and stays there.
        var bus = await FeedAsync(SfpCategory.Pon, On,
            (42.85, -22.37), (42.85, -22.44), (82.0, -21.0), (84.0, -20.9), (85.0, -20.8));

        bus.Events.Should().ContainSingle(e => e.EventType == "monitoring.sfp_temperature");
    }

    [Fact]
    public async Task ASlowClimbIsNotDelayedAtAll()
    {
        // No joint jump, so nothing is ever held back.
        var bus = await FeedAsync(SfpCategory.Pon, On,
            (68.0, -22.37), (71.0, -22.40), (74.0, -22.38), (77.0, -22.35));

        bus.Events.Should().ContainSingle(e => e.EventType == "monitoring.sfp_temperature");
    }

    [Fact]
    public async Task ActiveEthernetIsNeverDeferred()
    {
        // Same shape as the artifact, but on an AE module it is evaluated immediately.
        var bus = await FeedAsync(SfpCategory.ActiveEthernet, On,
            (42.85, -22.37), (95.0, -18.79), (44.85, -22.29));

        bus.Events.Should().Contain(e => e.EventType == "monitoring.sfp_temperature");
    }

    [Fact]
    public async Task TxPowerIsNeverHeldBack()
    {
        var bus = new CapturingBus();
        var evaluator = new SfpAlertEvaluator(bus, NullLogger<SfpAlertEvaluator>.Instance);

        await evaluator.EvaluateAsync(Mac, "7", "Gateway", SfpCategory.Pon, -22.37, -1.0, 42.85, On);
        // A joint temp+RX jump holds those two back; TX must still be judged on this poll.
        await evaluator.EvaluateAsync(Mac, "7", "Gateway", SfpCategory.Pon, -18.79, 99.0, 67.85, On);

        bus.Events.Should().ContainSingle(e => e.EventType == "monitoring.sfp_tx_power");
    }
}
