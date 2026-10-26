using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Alerts.Models;
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

    /// <summary>A reachability check that answers with whatever the test last set, counting calls.</summary>
    private sealed class FakeProbe : IDeviceReachabilityProbe
    {
        public DeviceReach Result = DeviceReach.Answered;
        public int Calls;

        public Task<DeviceReach> ProbeAsync(string siteSlug, string address, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private static (DeviceStateAlertEvaluator Evaluator, CapturingBus Bus, DeviceTransitionTracker Transitions) Build(
        IDeviceReachabilityProbe? probe = null, IServiceScopeFactory? stored = null)
    {
        var bus = new CapturingBus();
        var transitions = new DeviceTransitionTracker();
        return (new DeviceStateAlertEvaluator(bus, transitions, new DeviceOfflineDeduplicator(),
            NullLogger<DeviceStateAlertEvaluator>.Instance, scopeFactory: stored, reachability: probe), bus, transitions);
    }

    private static ValueTask Feed(DeviceStateAlertEvaluator e, int state, DateTime at, string mac = Mac) =>
        e.EvaluateAsync(mac, $"Device {mac[^2..]}", "192.0.2.10", DeviceType.Switch, state, at);

    /// <summary>An evaluator starting up with these alerts in the site's history.</summary>
    private static (DeviceStateAlertEvaluator Evaluator, CapturingBus Bus) BuildAfterRestart(params AlertHistoryEntry[] stored)
    {
        var (evaluator, bus, _) = Build(new FakeProbe(), OpenAlertTestScope.With(stored));
        return (evaluator, bus);
    }

    private static AlertHistoryEntry Stored(string eventType, TimeSpan ago, params (string, string)[] context) =>
        OpenAlertTestScope.Alert(eventType, "aa:bb:cc:dd:ee:ff", ago, context: context);

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
        Assert.Contains("Device ff", evt.Title);
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
    public async Task OfflineBeforeRestart_RecoversWhenBack()
    {
        var (evaluator, bus) = BuildAfterRestart(Stored("device.offline", TimeSpan.FromMinutes(5)));

        await Feed(evaluator, Connected, Now);

        Assert.Equal("device.recovered", bus.Published.Single().EventType);
    }

    [Fact]
    public async Task OfflineBeforeRestart_StillOffline_IsNotAnnouncedAgain()
    {
        var (evaluator, bus) = BuildAfterRestart(Stored("device.offline", TimeSpan.FromMinutes(5)));

        await Feed(evaluator, Disconnected, Now);
        await Feed(evaluator, Disconnected, Now.AddSeconds(30));
        await Feed(evaluator, Connected, Now.AddSeconds(60));

        Assert.Equal("device.recovered", bus.Published.Single().EventType);
    }

    [Fact]
    public async Task OfflineAlreadyRecoveredBeforeRestart_StaysQuiet()
    {
        var (evaluator, bus) = BuildAfterRestart(
            Stored("device.offline", TimeSpan.FromMinutes(10)), Stored("device.recovered", TimeSpan.FromMinutes(8)));

        await Feed(evaluator, Connected, Now);

        Assert.Empty(bus.Published);
    }

    /// <summary>A person resolving the recovery does not reopen the outage it closed.</summary>
    [Fact]
    public async Task OfflineWhoseRecoveryWasResolved_StaysQuiet()
    {
        var (evaluator, bus) = BuildAfterRestart(
            Stored("device.offline", TimeSpan.FromMinutes(10)), Stored("device.recovered", TimeSpan.FromMinutes(8)).Resolved());

        await Feed(evaluator, Connected, Now);

        Assert.Empty(bus.Published);
    }

    [Fact]
    public async Task UnmanagedBeforeRestart_PublishesManagedWhenBack()
    {
        var (evaluator, bus) = BuildAfterRestart(Stored("device.unmanaged", TimeSpan.FromMinutes(20)));

        await Feed(evaluator, Connected, Now);

        Assert.Equal("device.managed", bus.Published.Single().EventType);
    }

    [Fact]
    public async Task GroupBeforeRestart_PublishesGroupManagedWhenAllAreBack()
    {
        var (evaluator, bus) = BuildAfterRestart(OpenAlertTestScope.Alert("console.devices_unmanaged", "all-devices",
            TimeSpan.FromMinutes(20), context: ("devices", "aa:bb:cc:dd:ee:01,aa:bb:cc:dd:ee:02")));

        await PollAsync(evaluator, Now, Now.AddSeconds(30), All(Connected), "aabbccddee01", "aabbccddee02");

        Assert.Equal("console.devices_managed", bus.Published.Single().EventType);
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

    // --- Data-plane confirmation ---------------------------------------------------------------

    /// <summary>The check starts on the first observation, so the threshold never waits on it.</summary>
    [Fact]
    public async Task Probe_StartsOnTheFirstOfflineObservation()
    {
        var probe = new FakeProbe { Result = DeviceReach.Silent };
        var (evaluator, bus, _) = Build(probe);

        await Feed(evaluator, Disconnected, Now);

        Assert.Equal(1, probe.Calls);
        Assert.Empty(bus.Published);
    }

    [Theory]
    [InlineData(DeviceReach.Silent)]
    [InlineData(DeviceReach.CouldNotRun)]
    public async Task NoAnswer_PublishesOfflineAtTheThreshold(DeviceReach reach)
    {
        var (evaluator, bus, _) = Build(new FakeProbe { Result = reach });

        await Feed(evaluator, Disconnected, Now);
        await Feed(evaluator, Disconnected, Now.AddSeconds(30));

        Assert.Equal("device.offline", bus.Published.Single().EventType);
    }

    /// <summary>
    /// Polls every 30 s over [from, to), feeding every device and completing each pass, the way the
    /// medium tier does.
    /// </summary>
    private static async Task PollAsync(DeviceStateAlertEvaluator evaluator, DateTime from, DateTime to,
        Func<string, int> stateOf, params string[] macs)
    {
        for (var at = from; at < to; at = at.AddSeconds(30))
        {
            foreach (var mac in macs)
                await Feed(evaluator, stateOf(mac), at, mac);
            await evaluator.CompletePassAsync(at);
        }
    }

    private static Func<string, int> All(int state) => _ => state;

    private static readonly string[] Site4 = ["aabbccddee01", "aabbccddee02", "aabbccddee03", "aabbccddee04"];

    /// <summary>The case this exists for: a Network restart or inform change, traffic untouched.</summary>
    [Fact]
    public async Task Answers_IsHeldThenPublishedAsUnmanaged()
    {
        var (evaluator, bus, _) = Build(new FakeProbe());

        await PollAsync(evaluator, Now, Now.AddMinutes(9.5), All(Disconnected), Mac);
        Assert.Empty(bus.Published);

        await PollAsync(evaluator, Now.AddMinutes(9.5), Now.AddMinutes(10.5), All(Disconnected), Mac);

        var evt = bus.Published.Single();
        Assert.Equal("device.unmanaged", evt.EventType);
        Assert.Equal(AlertSeverity.Warning, evt.Severity);
        Assert.StartsWith("Not Reporting to UniFi Network:", evt.Title);
        Assert.Equal(Now.ToString("O"), evt.Context!["unmanaged_since"]);
    }

    [Fact]
    public async Task Answers_AndReinformsInsideTheHold_PublishesNothing()
    {
        var (evaluator, bus, _) = Build(new FakeProbe());

        await PollAsync(evaluator, Now, Now.AddMinutes(3), All(Disconnected), Mac);
        await PollAsync(evaluator, Now.AddMinutes(3), Now.AddMinutes(15), All(Connected), Mac);

        Assert.Empty(bus.Published);
    }

    /// <summary>A management-only device closes with device.managed, never a recovery from an outage nobody was told about.</summary>
    [Fact]
    public async Task Unmanaged_ThenReinforms_PublishesManagedOnly()
    {
        var (evaluator, bus, _) = Build(new FakeProbe());

        await PollAsync(evaluator, Now, Now.AddMinutes(10.5), All(Disconnected), Mac);
        await PollAsync(evaluator, Now.AddMinutes(10.5), Now.AddMinutes(11), All(Connected), Mac);

        Assert.Equal(["device.unmanaged", "device.managed"], bus.Published.Select(e => e.EventType));
    }

    /// <summary>A device removed from the console while held is never fed again, so it is never alerted on.</summary>
    [Fact]
    public async Task HeldDevice_RemovedFromTheConsole_IsNotAlertedOn()
    {
        var (evaluator, bus, _) = Build(new FakeProbe());

        await PollAsync(evaluator, Now, Now.AddMinutes(2), mac => mac == Mac ? Disconnected : Connected, [Mac, .. Site4]);
        await PollAsync(evaluator, Now.AddMinutes(2), Now.AddMinutes(20), All(Connected), Site4);

        Assert.Empty(bus.Published);
    }

    /// <summary>Traffic stopping while held is an outage, reported at once rather than at the hold's end.</summary>
    [Fact]
    public async Task Held_ThenStopsAnswering_PublishesOfflineImmediately()
    {
        var probe = new FakeProbe();
        var (evaluator, bus, _) = Build(probe);

        await Feed(evaluator, Disconnected, Now);
        await Feed(evaluator, Disconnected, Now.AddSeconds(30));
        probe.Result = DeviceReach.Silent;
        await Feed(evaluator, Disconnected, Now.AddSeconds(60));
        await Feed(evaluator, Disconnected, Now.AddSeconds(90));

        var evt = bus.Published.Single();
        Assert.Equal("device.offline", evt.EventType);
        Assert.Equal(Now.ToString("O"), evt.Context!["unmanaged_since"]);

        await Feed(evaluator, Connected, Now.AddSeconds(120));
        Assert.Equal(["device.offline", "device.recovered"], bus.Published.Select(e => e.EventType));
    }

    [Fact]
    public async Task MostOfTheSiteHeld_CollapsesIntoOneAlertAndRecoversAsOne()
    {
        var (evaluator, bus, _) = Build(new FakeProbe());

        await PollAsync(evaluator, Now, Now.AddMinutes(10.5), All(Disconnected), Site4);

        var group = bus.Published.Single();
        Assert.Equal("console.devices_unmanaged", group.EventType);
        Assert.Equal("all-devices", group.DeviceId);
        Assert.Equal(string.Join(",", Site4), group.Context!["devices"]);

        await PollAsync(evaluator, Now.AddMinutes(10.5), Now.AddMinutes(11), All(Connected), Site4);

        Assert.Equal(["console.devices_unmanaged", "console.devices_managed"], bus.Published.Select(e => e.EventType));
    }

    [Fact]
    public async Task OneDeviceOfSeveralHeld_IsAlertedOnItsOwn()
    {
        var (evaluator, bus, _) = Build(new FakeProbe());

        await PollAsync(evaluator, Now, Now.AddMinutes(10.5), mac => mac == Mac ? Disconnected : Connected, [Mac, .. Site4]);

        var evt = bus.Published.Single();
        Assert.Equal("device.unmanaged", evt.EventType);
        Assert.Equal(Mac, evt.DeviceId);
    }

    /// <summary>Once the group's premise is gone the few still out are alerted on their own.</summary>
    [Fact]
    public async Task Group_MostReinform_SplitsTheRestIntoTheirOwnAlerts()
    {
        var (evaluator, bus, _) = Build(new FakeProbe());

        await PollAsync(evaluator, Now, Now.AddMinutes(10.5), All(Disconnected), Site4);
        await PollAsync(evaluator, Now.AddMinutes(10.5), Now.AddMinutes(11),
            mac => mac == Site4[3] ? Disconnected : Connected, Site4);

        Assert.Equal(["console.devices_unmanaged", "console.devices_managed", "device.unmanaged"],
            bus.Published.Select(e => e.EventType));
        Assert.Equal(Site4[3], bus.Published[2].DeviceId);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(4, 3)]
    [InlineData(6, 3)]
    [InlineData(7, 4)]
    [InlineData(10, 5)]
    public void CollapseAt_IsHalfTheSiteAndNeverBelowThree(int active, int expected) =>
        Assert.Equal(expected, DeviceStateAlertEvaluator.CollapseAt(active));

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
