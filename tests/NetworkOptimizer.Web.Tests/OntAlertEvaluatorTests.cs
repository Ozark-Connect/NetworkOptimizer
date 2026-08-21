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
