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
