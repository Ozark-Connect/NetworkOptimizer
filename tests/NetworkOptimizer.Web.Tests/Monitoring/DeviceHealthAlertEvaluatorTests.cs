using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Alerts;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

public class DeviceHealthAlertEvaluatorTests
{
    private const string Mac = "aa:bb:cc:dd:ee:ff";

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

    private sealed class NoopSiteScope : IAlertSiteScope
    {
        public void UseSite(string? siteSlug) { }
    }

    private static AlertRule Rule(string pattern, double? threshold, bool enabled = true) => new()
    {
        Name = pattern,
        EventTypePattern = pattern,
        Source = "device",
        IsEnabled = enabled,
        ThresholdPercent = threshold
    };

    private static (DeviceHealthAlertEvaluator Evaluator, CapturingBus Bus) Build(params AlertRule[] rules)
    {
        var repo = new Mock<IAlertRepository>();
        repo.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(rules.Where(r => r.IsEnabled).ToList());

        var services = new ServiceCollection();
        services.AddScoped<IAlertSiteScope, NoopSiteScope>();
        services.AddScoped(_ => repo.Object);
        var provider = services.BuildServiceProvider();

        var bus = new CapturingBus();
        var evaluator = new DeviceHealthAlertEvaluator(bus, NullLogger<DeviceHealthAlertEvaluator>.Instance,
            scopeFactory: provider.GetRequiredService<IServiceScopeFactory>());
        return (evaluator, bus);
    }

    private static async Task FeedCpu(DeviceHealthAlertEvaluator evaluator, double cpu, int samples = 5)
    {
        for (var i = 0; i < samples; i++)
            await evaluator.EvaluateAsync(Mac, "Gateway", "gateway", cpu, null);
    }

    [Fact]
    public async Task Cpu_UsesRuleThreshold_NotDefault()
    {
        var (evaluator, bus) = Build(Rule(DeviceHealthAlertEvaluator.HighCpuEventType, 80));

        await FeedCpu(evaluator, 75);
        bus.Published.Should().BeEmpty();

        await FeedCpu(evaluator, 82);
        var evt = bus.Published.Should().ContainSingle().Subject;
        evt.ThresholdValue.Should().Be(80);
        evt.Message.Should().Contain("exceeding the 80% threshold");
        evt.Context[AlertRuleEvaluator.ValuePercentContextKey].Should().Be(evt.MetricValue!.Value.ToString("0.###"));
    }

    [Fact]
    public async Task Cpu_ClearsFifteenBelowRuleThreshold()
    {
        var (evaluator, bus) = Build(Rule(DeviceHealthAlertEvaluator.HighCpuEventType, 80));

        await FeedCpu(evaluator, 85);
        await FeedCpu(evaluator, 70);
        await FeedCpu(evaluator, 85);
        bus.Published.Should().HaveCount(1, "70% is above the 65% clear level, so the alert has not re-armed");

        await FeedCpu(evaluator, 65);
        await FeedCpu(evaluator, 85);
        bus.Published.Should().HaveCount(2);
    }

    [Fact]
    public async Task Cpu_NoRuleThreshold_UsesDefault()
    {
        var (evaluator, bus) = Build(Rule(DeviceHealthAlertEvaluator.HighCpuEventType, null));

        await FeedCpu(evaluator, 71);

        bus.Published.Should().ContainSingle().Which.ThresholdValue.Should().Be(70);
    }

    [Fact]
    public async Task Cpu_DisabledRuleThreshold_Ignored()
    {
        var (evaluator, bus) = Build(Rule(DeviceHealthAlertEvaluator.HighCpuEventType, 50, enabled: false));

        await FeedCpu(evaluator, 60);

        bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Cpu_SeveralRules_RaisedAtLowestThreshold()
    {
        var (evaluator, bus) = Build(
            Rule(DeviceHealthAlertEvaluator.HighCpuEventType, 90),
            Rule("device.*", 60));

        await FeedCpu(evaluator, 62);

        bus.Published.Should().ContainSingle().Which.ThresholdValue.Should().Be(60);
    }

    [Fact]
    public async Task Memory_UsesRuleThreshold()
    {
        var (evaluator, bus) = Build(Rule(DeviceHealthAlertEvaluator.HighMemoryEventType, 90));

        await evaluator.EvaluateAsync(Mac, "Gateway", "gateway", null, 91);

        var evt = bus.Published.Should().ContainSingle().Subject;
        evt.ThresholdValue.Should().Be(90);
        evt.Message.Should().Contain("exceeding the 90% threshold");
    }

    [Fact]
    public async Task NoScopeFactory_UsesDefaults()
    {
        var bus = new CapturingBus();
        var evaluator = new DeviceHealthAlertEvaluator(bus, NullLogger<DeviceHealthAlertEvaluator>.Instance);

        await evaluator.EvaluateAsync(Mac, "Gateway", "gateway", null, 94);
        bus.Published.Should().BeEmpty();

        await evaluator.EvaluateAsync(Mac, "Gateway", "gateway", null, 95);
        bus.Published.Should().ContainSingle().Which.ThresholdValue.Should().Be(95);
    }
}
