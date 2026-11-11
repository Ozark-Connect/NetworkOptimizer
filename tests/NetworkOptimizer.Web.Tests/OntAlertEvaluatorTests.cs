using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class OntAlertEvaluatorTests
{
    private const string TempEvent = "ont.high_temperature";

    // A safe RX reading (above the -25 dBm default) and an operational PON link so the
    // temperature assertions aren't polluted by rx_power_low / pon_link_down events.
    private const double SafeRx = -10.0;

    private static (OntAlertEvaluator Evaluator, CapturingBus Bus) Create()
    {
        var bus = new CapturingBus();
        var evaluator = new OntAlertEvaluator(bus, NullLogger<OntAlertEvaluator>.Instance);
        return (evaluator, bus);
    }

    [Fact]
    public async Task TempAboveThreshold_PublishesHighTemperatureOnce_ThenHoldsWhileBreached()
    {
        var (evaluator, bus) = Create();

        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, null,
            temperatureC: 80, tempHighC: 75);
        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, null,
            temperatureC: 82, tempHighC: 75);

        var temp = bus.Events.Where(e => e.EventType == TempEvent).ToList();
        temp.Should().HaveCount(1);
        temp[0].Source.Should().Be("ont");
        temp[0].Severity.Should().Be(AlertSeverity.Warning);
        temp[0].MetricValue.Should().Be(80);
        temp[0].ThresholdValue.Should().Be(75);
    }

    [Fact]
    public async Task TempRecoversBelowHysteresis_ThenRebreaches_PublishesAgain()
    {
        var (evaluator, bus) = Create();

        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, null,
            temperatureC: 80, tempHighC: 75);
        // Hysteresis is 5 C, so it only clears at or below 70.
        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, null,
            temperatureC: 69, tempHighC: 75);
        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, null,
            temperatureC: 81, tempHighC: 75);

        bus.Events.Count(e => e.EventType == TempEvent).Should().Be(2);
    }

    [Fact]
    public async Task TempWithinHysteresisBand_DoesNotClearOrRepublish()
    {
        var (evaluator, bus) = Create();

        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, null,
            temperatureC: 80, tempHighC: 75);
        // 72 is below the 75 ceiling but above the 70 clear point, so still breached.
        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, null,
            temperatureC: 72, tempHighC: 75);
        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, null,
            temperatureC: 78, tempHighC: 75);

        bus.Events.Count(e => e.EventType == TempEvent).Should().Be(1);
    }

    [Fact]
    public async Task UsesSuppliedThreshold_NotTheDefault()
    {
        var (evaluator, bus) = Create();

        // 65 C is under the 75 C default but over the supplied 60 C threshold.
        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, null,
            temperatureC: 65, tempHighC: 60);

        var temp = bus.Events.Where(e => e.EventType == TempEvent).ToList();
        temp.Should().HaveCount(1);
        temp[0].ThresholdValue.Should().Be(60);
    }

    [Fact]
    public async Task NullTemperature_PublishesNoTemperatureEvent()
    {
        var (evaluator, bus) = Create();

        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, null,
            temperatureC: null, tempHighC: 75);

        bus.Events.Should().NotContain(e => e.EventType == TempEvent);
    }

    [Fact]
    public async Task BipSpike_FecDisabled_UsesStrictThreshold()
    {
        var (evaluator, bus) = Create();

        // FEC off: BIP is uncorrected data loss, so the strict 25-error threshold applies.
        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, null, bipErrors: 0, fecEnabled: false);
        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, null, bipErrors: 50, fecEnabled: false);

        var bip = bus.Events.Where(e => e.EventType == "ont.bip_errors").ToList();
        bip.Should().HaveCount(1);
        bip[0].Source.Should().Be("ont");
        bip[0].MetricValue.Should().Be(50);
        bip[0].ThresholdValue.Should().Be(25);
    }

    [Fact]
    public async Task BipSpike_FecEnabled_UsesRelaxedThreshold()
    {
        var (evaluator, bus) = Create();

        // FEC on: BIP counts pre-FEC line errors FEC corrects, so a 300-error step (below the
        // relaxed 1000 threshold) must NOT alert, while a larger 1100-error step does.
        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, null, bipErrors: 0, fecEnabled: true);
        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, null, bipErrors: 300, fecEnabled: true);
        bus.Events.Should().NotContain(e => e.EventType == "ont.bip_errors");

        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, null, bipErrors: 1400, fecEnabled: true);
        var bip = bus.Events.Where(e => e.EventType == "ont.bip_errors").ToList();
        bip.Should().HaveCount(1);
        bip[0].MetricValue.Should().Be(1100);
        bip[0].ThresholdValue.Should().Be(1000);
    }

    [Fact]
    public async Task BipSpike_NothingUncorrectable_DropsToInfo()
    {
        var (evaluator, bus) = Create();

        // FEC on and absorbing all of it: the link is degrading, not losing data, so the spike
        // still reports but at Info rather than Warning.
        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, 0, bipErrors: 0, fecEnabled: true);
        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, 0, bipErrors: 1400, fecEnabled: true);

        var bip = bus.Events.Where(e => e.EventType == "ont.bip_errors").ToList();
        bip.Should().HaveCount(1);
        bip[0].Severity.Should().Be(AlertSeverity.Info);
        bip[0].MetricValue.Should().Be(1400);
    }

    [Fact]
    public async Task BipSpike_WithUncorrectableCodewords_StaysWarning()
    {
        var (evaluator, bus) = Create();

        // Uncorrectable codewords in the same interval: something reached the payload, so the
        // spike is corroborated and keeps its full severity.
        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, 0, bipErrors: 0, fecEnabled: true);
        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, 3, bipErrors: 1400, fecEnabled: true);

        var bip = bus.Events.Where(e => e.EventType == "ont.bip_errors").ToList();
        bip.Should().HaveCount(1);
        bip[0].Severity.Should().Be(AlertSeverity.Warning);
    }

    [Fact]
    public async Task BipSpike_FecDisabled_ChecksHecNotFec()
    {
        var (evaluator, bus) = Create();

        // With FEC off the corroborating counter is HEC, and it moved here, so Warning stands
        // even though the FEC counter beside it never budges on such a link.
        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, 0, bipErrors: 0,
            hecErrors: 0, fecEnabled: false);
        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, 0, bipErrors: 50,
            hecErrors: 2, fecEnabled: false);

        var bip = bus.Events.Where(e => e.EventType == "ont.bip_errors").ToList();
        bip.Should().HaveCount(1);
        bip[0].Severity.Should().Be(AlertSeverity.Warning);
    }

    [Fact]
    public async Task BipSpike_NoUncorrectableCounterReported_KeepsWarning()
    {
        var (evaluator, bus) = Create();

        // Nothing to check the spike against, so it is not quietly discounted.
        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, null, bipErrors: 0, fecEnabled: false);
        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, null, bipErrors: 50, fecEnabled: false);

        var bip = bus.Events.Where(e => e.EventType == "ont.bip_errors").ToList();
        bip.Should().HaveCount(1);
        bip[0].Severity.Should().Be(AlertSeverity.Warning);
    }

    [Fact]
    public async Task BipCounterReset_DoesNotFakeSpike()
    {
        var (evaluator, bus) = Create();

        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, null, bipErrors: 5000, fecEnabled: false);
        // ONT reboots; counter resets below the prior value -> negative step -> no alert.
        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, null, bipErrors: 10, fecEnabled: false);

        bus.Events.Should().NotContain(e => e.EventType == "ont.bip_errors");
    }

    [Fact]
    public async Task FecDisabled_EvaluatesHecNotFec()
    {
        var (evaluator, bus) = Create();

        // With FEC disabled, a large FEC delta must be ignored and HEC drives the codeword alert.
        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, fecErrors: 0,
            hecErrors: 0, fecEnabled: false);
        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, fecErrors: 100000,
            hecErrors: 500, fecEnabled: false);

        bus.Events.Should().NotContain(e => e.EventType == "ont.fec_errors");
        var hec = bus.Events.Where(e => e.EventType == "ont.hec_errors").ToList();
        hec.Should().HaveCount(1);
        hec[0].MetricValue.Should().Be(500);
    }

    [Fact]
    public async Task FecEnabledOrUnknown_EvaluatesFecNotHec()
    {
        var (evaluator, bus) = Create();

        // Default (fecEnabled unknown/null, the standalone-ONT case): FEC drives the alert, HEC is ignored.
        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, fecErrors: 0, hecErrors: 0);
        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, fecErrors: 2000, hecErrors: 9999);

        bus.Events.Should().NotContain(e => e.EventType == "ont.hec_errors");
        bus.Events.Count(e => e.EventType == "ont.fec_errors").Should().Be(1);
    }

    [Fact]
    public async Task UptimeGoesBackwards_PublishesRebootedOnce()
    {
        var (evaluator, bus) = Create();

        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, null, uptimeSeconds: 5000);
        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, null, uptimeSeconds: 5300);
        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, null, uptimeSeconds: 40);
        await evaluator.EvaluateAsync(1, "ONT", SafeRx, PonLinkState.Operation, null, uptimeSeconds: 340);

        var reboot = bus.Events.Where(e => e.EventType == "ont.rebooted").ToList();
        reboot.Should().HaveCount(1, "the first reading is a baseline and only the drop is a reboot");
        reboot[0].MetricValue.Should().Be(40);
    }

    [Fact]
    public async Task ManagementUnresponsive_FiresAfterThreeFailedPollsWithLight()
    {
        var (evaluator, bus) = Create();

        for (var i = 0; i < PonThresholds.OntManagementFailedPolls - 1; i++)
            await evaluator.EvaluateManagementAsync(1, "ONT", pollSucceeded: false, ddmRxPowerDbm: -20, rxPowerLowDbm: -25);
        bus.Events.Should().BeEmpty("one or two misses are a slow page, not a hang");

        await evaluator.EvaluateManagementAsync(1, "ONT", pollSucceeded: false, ddmRxPowerDbm: -20, rxPowerLowDbm: -25);
        await evaluator.EvaluateManagementAsync(1, "ONT", pollSucceeded: false, ddmRxPowerDbm: -20, rxPowerLowDbm: -25);

        bus.Events.Count(e => e.EventType == "ont.management_unresponsive").Should().Be(1);
    }

    [Fact]
    public async Task ManagementUnresponsive_NoLight_IsAFiberFaultAndDoesNotFire()
    {
        var (evaluator, bus) = Create();

        for (var i = 0; i < 5; i++)
            await evaluator.EvaluateManagementAsync(1, "ONT", pollSucceeded: false, ddmRxPowerDbm: -30, rxPowerLowDbm: -25);
        for (var i = 0; i < 5; i++)
            await evaluator.EvaluateManagementAsync(2, "ONT2", pollSucceeded: false, ddmRxPowerDbm: null, rxPowerLowDbm: -25);

        bus.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task ManagementUnresponsive_SuccessResets_ThenCanFireAgain()
    {
        var (evaluator, bus) = Create();

        for (var i = 0; i < 3; i++)
            await evaluator.EvaluateManagementAsync(1, "ONT", false, -20, -25);
        await evaluator.EvaluateManagementAsync(1, "ONT", true, -20, -25);
        for (var i = 0; i < 2; i++)
            await evaluator.EvaluateManagementAsync(1, "ONT", false, -20, -25);
        bus.Events.Count.Should().Be(1, "the count restarts after a successful poll");

        await evaluator.EvaluateManagementAsync(1, "ONT", false, -20, -25);
        bus.Events.Count(e => e.EventType == "ont.management_unresponsive").Should().Be(2);
    }

    private static readonly DateTime PathT0 = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private static async Task Feed(OntAlertEvaluator evaluator, int start, params double[] losses)
    {
        for (var i = 0; i < losses.Length; i++)
            await evaluator.EvaluatePathLossAsync(1, "ONT", losses[i], 2, now: PathT0.AddMinutes(5 * (start + i)));
    }

    [Fact]
    public async Task PathLoss_SteadyInsideTheBudget_RaisesNothing()
    {
        var (evaluator, bus) = Create();

        await Feed(evaluator, 0, Enumerable.Range(0, 40).Select(i => 19.8 + 0.1 * (i % 2)).ToArray());

        bus.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task PathLoss_RiseAboveTheLearnedLevel_RaisesOnce_AndTheLevelStandsStill()
    {
        var (evaluator, bus) = Create();

        await Feed(evaluator, 0, Enumerable.Repeat(19.8, 12).ToArray());
        // Two readings up are not yet "now"; the third is.
        await Feed(evaluator, 12, 22.3, 22.3);
        bus.Events.Should().BeEmpty();
        await Feed(evaluator, 14, Enumerable.Repeat(22.3, 300).ToArray());

        var rise = bus.Events.Where(e => e.EventType == "ont.path_loss_rise").ToList();
        rise.Should().HaveCount(1, "a day at the higher level must not become the usual one");
        rise[0].Severity.Should().Be(AlertSeverity.Warning);
        rise[0].MetricValue.Should().Be(22.3);
        rise[0].Context!["ont_id"].Should().Be("1");
        bus.Events.Should().OnlyContain(e => e.EventType == "ont.path_loss_rise", "22.3 dB is well inside class B+");
    }

    [Fact]
    public async Task PathLoss_OneOutlierPoll_RaisesNothing()
    {
        var (evaluator, bus) = Create();

        await Feed(evaluator, 0, Enumerable.Repeat(19.8, 12).ToArray());
        await Feed(evaluator, 12, 23.0, 19.8, 19.8, 19.8);

        bus.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task PathLoss_RiseClearsWithHysteresis_ThenCanRaiseAgain()
    {
        var (evaluator, bus) = Create();

        await Feed(evaluator, 0, Enumerable.Repeat(19.8, 12).ToArray());
        await Feed(evaluator, 12, 22.0, 22.0, 22.0);
        // 1.6 dB above the level is still inside the hysteresis; 1.0 dB is back.
        await Feed(evaluator, 15, 21.4, 21.4, 21.4);
        await Feed(evaluator, 18, 22.0, 22.0, 22.0);
        bus.Events.Count(e => e.EventType == "ont.path_loss_rise").Should().Be(1);

        await Feed(evaluator, 21, 20.8, 20.8, 20.8);
        await Feed(evaluator, 24, 22.9, 22.9, 22.9);
        bus.Events.Count(e => e.EventType == "ont.path_loss_rise").Should().Be(2);
    }

    [Fact]
    public async Task PathLoss_SlowDrift_IsFollowedRatherThanRaised()
    {
        var (evaluator, bus) = Create();

        // 3 dB over thirty days at five-minute polls: a season, not a fault.
        var n = 30 * 288;
        await Feed(evaluator, 0, Enumerable.Range(0, n).Select(i => 18.0 + 3.0 * i / n).ToArray());

        bus.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task PathLoss_NearTheClassLimit_WarnsOnce_ThenErrorsWhenOver()
    {
        var (evaluator, bus) = Create();

        await Feed(evaluator, 0, 25.6, 25.6, 25.6, 25.7);
        var high = bus.Events.Where(e => e.EventType == "ont.path_loss_high").ToList();
        high.Should().HaveCount(1);
        high[0].Severity.Should().Be(AlertSeverity.Warning);
        high[0].ThresholdValue.Should().Be(25);
        high[0].Context!["odn_class"].Should().Be("B+");

        await Feed(evaluator, 4, 28.4, 28.4, 28.4);
        high = bus.Events.Where(e => e.EventType == "ont.path_loss_high").ToList();
        high.Should().HaveCount(2);
        high[1].Severity.Should().Be(AlertSeverity.Error);
        high[1].ThresholdValue.Should().Be(28);
    }

    [Fact]
    public async Task PathLoss_BudgetAlertClearsWithHysteresis()
    {
        var (evaluator, bus) = Create();

        await Feed(evaluator, 0, 25.6, 25.6, 25.6);
        // 24.8 dB is back under the reserve line, but not by the hysteresis.
        await Feed(evaluator, 3, 24.8, 24.8, 24.8, 25.6, 25.6, 25.6);
        bus.Events.Count(e => e.EventType == "ont.path_loss_high").Should().Be(1);

        await Feed(evaluator, 9, 24.2, 24.2, 24.2, 25.6, 25.6, 25.6);
        bus.Events.Count(e => e.EventType == "ont.path_loss_high").Should().Be(2);
    }

    [Fact]
    public async Task PathLoss_WithoutAClass_StillWatchesTheRise_AndWithoutALoss_DoesNothing()
    {
        var (evaluator, bus) = Create();

        for (var i = 0; i < 12; i++)
            await evaluator.EvaluatePathLossAsync(1, "ONT", 29.0, null, now: PathT0.AddMinutes(5 * i));
        await evaluator.EvaluatePathLossAsync(1, "ONT", null, 2, now: PathT0.AddMinutes(60));
        bus.Events.Should().BeEmpty("no class, no budget to hold the loss against");

        for (var i = 13; i < 16; i++)
            await evaluator.EvaluatePathLossAsync(1, "ONT", 31.5, null, now: PathT0.AddMinutes(5 * i));
        bus.Events.Should().ContainSingle().Which.EventType.Should().Be("ont.path_loss_rise");
    }

    private sealed class CapturingBus : IAlertEventBus
    {
        public List<AlertEvent> Events { get; } = new();

        public ValueTask PublishAsync(AlertEvent alertEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(alertEvent);
            return ValueTask.CompletedTask;
        }

        public IAsyncEnumerable<AlertEvent> ConsumeAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
