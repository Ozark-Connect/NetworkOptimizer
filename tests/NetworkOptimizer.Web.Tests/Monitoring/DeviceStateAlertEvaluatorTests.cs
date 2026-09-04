using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// UniFi device <c>state</c> values used here: 0 disconnected, 1 connected, 4 upgrading,
/// 5 provisioning.
/// </summary>
public class DeviceStateAlertEvaluatorTests
{
    private const int Disconnected = 0;
    private const int Connected = 1;
    private const int Upgrading = 4;
    private const int Provisioning = 5;

    private const string Mac = "aabbccddeeff";
    private static readonly DateTime Now = new(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);

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

    private static (DeviceStateAlertEvaluator Evaluator, CapturingBus Bus, DeviceTransitionTracker Transitions) Build()
    {
        var bus = new CapturingBus();
        var transitions = new DeviceTransitionTracker();
        return (new DeviceStateAlertEvaluator(bus, transitions, new DeviceOfflineDeduplicator(),
            NullLogger<DeviceStateAlertEvaluator>.Instance), bus, transitions);
    }

    private static ValueTask Feed(DeviceStateAlertEvaluator e, int state, DateTime at) =>
        e.EvaluateAsync(Mac, "Switch 1", "192.0.2.10", DeviceType.Switch, state, at);

    [Fact]
    public async Task SustainedOffline_PublishesOffline()
    {
        var (evaluator, bus, _) = Build();

        await Feed(evaluator, Disconnected, Now);
        Assert.Empty(bus.Published); // one sample is not enough

        await Feed(evaluator, Disconnected, Now.AddSeconds(30));

        var evt = bus.Published.Single();
        Assert.Equal("device.offline", evt.EventType);
        Assert.Equal(AlertSeverity.Error, evt.Severity);
        Assert.Contains("Switch 1", evt.Title);
        Assert.Equal(Mac, evt.DeviceId);
    }

    [Fact]
    public async Task OfflineThenBack_PublishesRecoveredOnce()
    {
        var (evaluator, bus, _) = Build();

        await Feed(evaluator, Disconnected, Now);
        await Feed(evaluator, Disconnected, Now.AddSeconds(30));
        await Feed(evaluator, Connected, Now.AddSeconds(60));
        await Feed(evaluator, Connected, Now.AddSeconds(90));

        Assert.Equal(2, bus.Published.Count);
        Assert.Equal("device.offline", bus.Published[0].EventType);
        Assert.Equal("device.recovered", bus.Published[1].EventType);
        Assert.Equal(AlertSeverity.Info, bus.Published[1].Severity);
    }

    [Fact]
    public async Task Offline_DoesNotRepeatWhileStillOffline()
    {
        var (evaluator, bus, _) = Build();

        for (var i = 0; i < 10; i++)
            await Feed(evaluator, Disconnected, Now.AddSeconds(30 * i));

        Assert.Single(bus.Published);
    }

    [Theory]
    [InlineData(Upgrading)]
    [InlineData(Provisioning)]
    public async Task TransitionalState_NeverAlerts(int state)
    {
        var (evaluator, bus, _) = Build();

        for (var i = 0; i < 5; i++)
            await Feed(evaluator, state, Now.AddSeconds(30 * i));

        Assert.Empty(bus.Published);
    }

    /// <summary>
    /// The case this feature exists for: UniFi flips a device to Offline partway through a firmware
    /// install, after reporting it Upgrading. The recent transition has to keep that quiet.
    /// </summary>
    [Fact]
    public async Task OfflineJustAfterAnUpgrade_IsNotAnOutage()
    {
        var (evaluator, bus, transitions) = Build();

        transitions.Record("", Mac, Upgrading, Now);
        await Feed(evaluator, Upgrading, Now);

        await Feed(evaluator, Disconnected, Now.AddSeconds(30));
        await Feed(evaluator, Disconnected, Now.AddSeconds(60));

        Assert.Empty(bus.Published);
    }

    /// <summary>
    /// Suppression must lapse: a device still dark long after its upgrade window is a real outage.
    /// </summary>
    [Fact]
    public async Task OfflineLongAfterAnUpgrade_IsAnOutage()
    {
        var (evaluator, bus, transitions) = Build();

        transitions.Record("", Mac, Upgrading, Now);
        var later = Now + DeviceTransitionTracker.ObservationFreshness + TimeSpan.FromMinutes(1);

        await Feed(evaluator, Disconnected, later);
        await Feed(evaluator, Disconnected, later.AddSeconds(30));

        Assert.Equal("device.offline", bus.Published.Single().EventType);
    }

    /// <summary>
    /// An outage already announced that turns out to be an upgrade closes as recovered, so the
    /// alert does not sit open once the cause is known and benign.
    /// </summary>
    [Fact]
    public async Task AnnouncedOutageThatBecomesAnUpgrade_Recovers()
    {
        var (evaluator, bus, _) = Build();

        await Feed(evaluator, Disconnected, Now);
        await Feed(evaluator, Disconnected, Now.AddSeconds(30));
        await Feed(evaluator, Upgrading, Now.AddSeconds(60));

        Assert.Equal(2, bus.Published.Count);
        Assert.Equal("device.recovered", bus.Published[1].EventType);
        Assert.Contains("expected restart", bus.Published[1].Message);
    }

    [Fact]
    public async Task NonDefaultSite_StampsSlugInTitle()
    {
        var bus = new CapturingBus();
        var evaluator = new DeviceStateAlertEvaluator(bus, new DeviceTransitionTracker(), new DeviceOfflineDeduplicator(),
            NullLogger<DeviceStateAlertEvaluator>.Instance, "branch-office");

        await evaluator.EvaluateAsync(Mac, "AP 1", "192.0.2.11", DeviceType.AccessPoint, Disconnected, Now);
        await evaluator.EvaluateAsync(Mac, "AP 1", "192.0.2.11", DeviceType.AccessPoint, Disconnected, Now.AddSeconds(30));

        Assert.Contains("(site branch-office)", bus.Published.Single().Title);
    }

    [Fact]
    public void Transitions_AreScopedPerSite()
    {
        var transitions = new DeviceTransitionTracker();

        transitions.Record("site-a", Mac, Upgrading, Now);

        Assert.True(transitions.IsInKnownTransition("site-a", Mac, Now));
        Assert.False(transitions.IsInKnownTransition("site-b", Mac, Now));
    }

    [Fact]
    public void Transitions_ClearAsSoonAsTheDeviceIsBack()
    {
        var transitions = new DeviceTransitionTracker();

        transitions.Record("", Mac, Upgrading, Now);
        transitions.Record("", Mac, Connected, Now.AddSeconds(30));

        Assert.False(transitions.IsInKnownTransition("", Mac, Now.AddSeconds(30)));
    }

    [Fact]
    public void Transitions_GoStaleSoSuppressionCannotOutliveObservation()
    {
        var transitions = new DeviceTransitionTracker();

        transitions.Record("", Mac, Upgrading, Now);

        Assert.False(transitions.IsInKnownTransition(
            "", Mac, Now + DeviceTransitionTracker.ObservationFreshness + TimeSpan.FromSeconds(1)));
    }
}
