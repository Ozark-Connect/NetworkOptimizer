using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Web.Services.Monitoring;
using NetworkOptimizer.Web.Services.Monitoring.RebootReason;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

public class DeviceRebootAlertEvaluatorTests
{
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

    private static readonly DateTime Now = new(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);

    private static (DeviceRebootAlertEvaluator Evaluator, CapturingBus Bus) Build()
    {
        var bus = new CapturingBus();
        return (new DeviceRebootAlertEvaluator(bus, NullLogger<DeviceRebootAlertEvaluator>.Instance), bus);
    }

    private static DeviceRebootReason Reason(RebootCategory category) =>
        new(category, category.ToString(), "evidence", RebootReasonSource.PstoreConsole);

    [Theory]
    [InlineData(RebootCategory.PowerLoss)]
    [InlineData(RebootCategory.AbruptStop)]
    [InlineData(RebootCategory.KernelPanic)]
    [InlineData(RebootCategory.HardwareHang)]
    [InlineData(RebootCategory.Watchdog)]
    public async Task UnexpectedReboot_PublishesWarning(RebootCategory category)
    {
        var (evaluator, bus) = Build();

        var published = await evaluator.EvaluateAsync(
            "aabbccddeeff", "Switch A", "192.0.2.10", Reason(category),
            bootedAt: Now.AddMinutes(-2), now: Now);

        Assert.True(published);
        Assert.Equal(AlertSeverity.Warning, bus.Published.Single().Severity);
        Assert.Equal("device.rebooted", bus.Published.Single().EventType);
    }

    [Theory]
    [InlineData(RebootCategory.CommandedReboot)]
    [InlineData(RebootCategory.FirmwareUpgrade)]
    [InlineData(RebootCategory.PowerCycle)]
    public async Task DeliberateReboot_PublishesInfo(RebootCategory category)
    {
        var (evaluator, bus) = Build();

        var published = await evaluator.EvaluateAsync(
            "aabbccddeeff", "Switch A", "192.0.2.10", Reason(category),
            bootedAt: Now.AddMinutes(-2), now: Now);

        Assert.True(published);
        Assert.Equal(AlertSeverity.Info, bus.Published.Single().Severity);
    }

    /// <summary>
    /// Backfill resolves reasons for devices up for weeks. Those must not alert, or first run and
    /// every server restart would fire a burst of notifications about history.
    /// </summary>
    [Fact]
    public async Task OldBoot_DoesNotAlert()
    {
        var (evaluator, bus) = Build();

        var published = await evaluator.EvaluateAsync(
            "aabbccddeeff", "Switch A", "192.0.2.10", Reason(RebootCategory.PowerLoss),
            bootedAt: Now.AddDays(-9), now: Now);

        Assert.False(published);
        Assert.Empty(bus.Published);
    }

    [Fact]
    public async Task BootJustInsideWindow_Alerts()
    {
        var (evaluator, bus) = Build();

        var published = await evaluator.EvaluateAsync(
            "aabbccddeeff", "Switch A", "192.0.2.10", Reason(RebootCategory.PowerLoss),
            bootedAt: Now.Add(-DeviceRebootAlertEvaluator.CurrentBootWindow).AddMinutes(1), now: Now);

        Assert.True(published);
        Assert.Single(bus.Published);
    }

    [Fact]
    public async Task InconclusiveReason_DoesNotAlert()
    {
        var (evaluator, bus) = Build();

        var published = await evaluator.EvaluateAsync(
            "aabbccddeeff", "Switch A", "192.0.2.10", DeviceRebootReason.Unknown(),
            bootedAt: Now.AddMinutes(-2), now: Now);

        Assert.False(published);
        Assert.Empty(bus.Published);
    }

    [Fact]
    public async Task Alert_CarriesDeviceIdentityAndReason()
    {
        var (evaluator, bus) = Build();

        await evaluator.EvaluateAsync(
            "aabbccddeeff", "Switch A", "192.0.2.10",
            new DeviceRebootReason(RebootCategory.PowerLoss, "Power loss",
                "Restart register: Power on Reset [0x20]", RebootReasonSource.ConsoleRebootLog),
            bootedAt: Now.AddMinutes(-3), now: Now);

        var evt = bus.Published.Single();
        Assert.Equal("aabbccddeeff", evt.DeviceId);
        Assert.Equal("Switch A", evt.DeviceName);
        Assert.Equal("192.0.2.10", evt.DeviceIp);
        Assert.Contains("Switch A", evt.Title);
        Assert.Contains("Power loss", evt.Message);
        Assert.Contains("0x20", evt.Message);
    }

    [Fact]
    public async Task NonDefaultSite_StampsSlugInTitle()
    {
        var bus = new CapturingBus();
        var evaluator = new DeviceRebootAlertEvaluator(
            bus, NullLogger<DeviceRebootAlertEvaluator>.Instance, "atl-1365");

        await evaluator.EvaluateAsync(
            "aabbccddeeff", "Switch A", "192.0.2.10", Reason(RebootCategory.PowerLoss),
            bootedAt: Now.AddMinutes(-1), now: Now);

        Assert.Contains("(site atl-1365)", bus.Published.Single().Title);
    }
}
