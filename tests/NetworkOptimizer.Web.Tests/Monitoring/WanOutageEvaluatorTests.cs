using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Probes;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// The state-machine half of the WAN outage alert family, driven the way production drives it:
/// probe results into <see cref="MonitoringAlertEvaluator"/>, which runs the per-target machines
/// and hands the WAN-facing ones to <see cref="WanOutageEvaluator"/>. What these pin down is the
/// promise the feature makes - one notification per event instead of one per target: an outage
/// publishes once, a partial that becomes total is superseded rather than stacked, a whole site
/// going dark collapses into a single rollup that releases back to per-WAN alerts as soon as the
/// WANs differ again, and both a flap and a monitoring gap publish nothing at all. Fabric and
/// custom targets keep their per-target events throughout.
///
/// Time is faked because both machines are cadence-driven: a target needs three consecutive
/// failed probes, and the WAN verdict then has to hold three evaluation passes 30 seconds apart.
/// Probes arrive faster than passes run, exactly as they do in production (10 s polling against
/// the 30 s pass throttle), which is what lets the flap test move a verdict in and out inside a
/// single evaluation window.
/// </summary>
public class WanOutageEvaluatorTests
{
    /// <summary>
    /// Probe rounds comfortably past what it takes to open or close an alert. Opening needs two
    /// failed probes per target and two confirming passes; closing needs three successes to clear
    /// each per-target machine and three passes, so this is sized for the slower of the two. Extra
    /// rounds are harmless - the state machine only publishes on transitions.
    /// </summary>
    private const int RoundsToConfirm = 8;

    /// <summary>A hair over the evaluator's pass interval, so each round runs exactly one pass.</summary>
    private const int SecondsPerPass = 11;

    private static readonly DateTime Start = new(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);

    private static readonly MonitoringTarget WanAccess =
        Target("wan-access", "Acme Fiber first hop", "192.0.2.1", MonitoringTargetType.AccessIsp,
            "wan", 64500, "Acme Fiber");
    private static readonly MonitoringTarget WanTransit =
        Target("wan-transit", "TransitNet", "198.51.100.1", MonitoringTargetType.Transit,
            "wan", 64501, "TransitNet");
    private static readonly MonitoringTarget WanResolverA =
        Target("wan-resolver-a", "resolver-a", "203.0.113.10", MonitoringTargetType.InternetService,
            "wan", 64510, "Alpha Cloud");
    private static readonly MonitoringTarget WanResolverB =
        Target("wan-resolver-b", "resolver-b", "203.0.113.20", MonitoringTargetType.InternetService,
            "wan", 64520, "Beta Cloud");

    private static readonly MonitoringTarget Wan2Access =
        Target("wan2-access", "Beta Cable first hop", "192.0.2.65", MonitoringTargetType.AccessIsp,
            "wan2", 64600, "Beta Cable");
    private static readonly MonitoringTarget Wan2Transit =
        Target("wan2-transit", "TransitNet via Beta Cable", "198.51.100.65", MonitoringTargetType.Transit,
            "wan2", 64501, "TransitNet");
    private static readonly MonitoringTarget Wan2ResolverA =
        Target("wan2-resolver-a", "resolver-c", "203.0.113.65", MonitoringTargetType.InternetService,
            "wan2", 64510, "Alpha Cloud");
    private static readonly MonitoringTarget Wan2ResolverB =
        Target("wan2-resolver-b", "resolver-d", "203.0.113.75", MonitoringTargetType.InternetService,
            "wan2", 64520, "Beta Cloud");

    private static readonly MonitoringTarget[] WanTargets =
        [WanAccess, WanTransit, WanResolverA, WanResolverB];
    private static readonly MonitoringTarget[] Wan2Targets =
        [Wan2Access, Wan2Transit, Wan2ResolverA, Wan2ResolverB];
    private static readonly MonitoringTarget[] WanDestinations = [WanResolverA, WanResolverB];
    private static readonly MonitoringTarget[] WanPath = [WanAccess, WanTransit];
    private static readonly MonitoringTarget[] NoTargets = [];

    private readonly CapturingAlertEventBus _bus = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly MonitoringAlertEvaluator _evaluator;

    public WanOutageEvaluatorTests()
    {
        _evaluator = BuildEvaluator(BuildContext());
    }

    private MonitoringAlertEvaluator BuildEvaluator(WanOutageContext context)
    {
        var wanOutages = new WanOutageEvaluator(_bus, NullLogger<WanOutageEvaluator>.Instance,
            new FakeContextSource(context), timeProvider: _time);
        return new MonitoringAlertEvaluator(_bus, NullLogger<MonitoringAlertEvaluator>.Instance,
            new DeviceTransitionTracker(), wanOutages);
    }

    #region Per-WAN outages

    [Fact]
    public async Task EveryTargetOnOneWanFailing_PublishesOneOutageAndNoPerTargetEvents()
    {
        await RoundsAsync(RoundsToConfirm, failing: WanTargets, passing: Wan2Targets);

        _bus.Published.Should().ContainSingle();
        var evt = _bus.Published.Single();
        evt.EventType.Should().Be("monitoring.wan_outage");
        evt.Severity.Should().Be(AlertSeverity.Critical);
        evt.DeviceId.Should().Be("wan");
        evt.Title.Should().StartWith("Internet down on Acme Fiber WAN1");
        _bus.Published.Should().NotContain(e => e.EventType == "monitoring.target_offline");
    }

    [Fact]
    public async Task DestinationsFailingWhileThePathAnswers_PublishesOnePartialOutage()
    {
        await RoundsAsync(RoundsToConfirm, failing: WanDestinations,
            passing: [.. WanPath, .. Wan2Targets]);

        _bus.Published.Should().ContainSingle();
        var evt = _bus.Published.Single();
        evt.EventType.Should().Be("monitoring.wan_outage_partial");
        evt.Severity.Should().Be(AlertSeverity.Warning);
        evt.DeviceId.Should().Be("wan");
    }

    /// <summary>
    /// A partial that grows into the whole connection is superseded, never stacked: the total
    /// follows once, and the partial is not repeated as the picture worsens.
    /// </summary>
    [Fact]
    public async Task PartialThatBecomesTotal_IsFollowedByExactlyOneOutage()
    {
        await RoundsAsync(RoundsToConfirm, failing: WanDestinations,
            passing: [.. WanPath, .. Wan2Targets]);
        await RoundsAsync(RoundsToConfirm, failing: WanTargets, passing: Wan2Targets);

        _bus.Published.Select(e => e.EventType).Should()
            .Equal("monitoring.wan_outage_partial", "monitoring.wan_outage");
        _bus.Published[1].DeviceId.Should().Be("wan");
    }

    [Fact]
    public async Task WanThatComesBack_PublishesOneRecoveryAndThenStaysQuiet()
    {
        await RoundsAsync(RoundsToConfirm, failing: WanTargets, passing: Wan2Targets);
        await RoundsAsync(RoundsToConfirm, failing: NoTargets, passing: [.. WanTargets, .. Wan2Targets]);

        _bus.Published.Select(e => e.EventType).Should()
            .Equal("monitoring.wan_outage", "monitoring.wan_recovered");
        var recovered = _bus.Published[1];
        recovered.Severity.Should().Be(AlertSeverity.Info);
        recovered.DeviceId.Should().Be("wan");

        await RoundsAsync(RoundsToConfirm, failing: NoTargets, passing: [.. WanTargets, .. Wan2Targets]);

        _bus.Published.Should().HaveCount(2);
    }

    /// <summary>
    /// A backup WAN going dark matters, but not at the severity of the connection the site is
    /// actually using - and it says nothing about the WAN that is still passing traffic.
    /// </summary>
    [Fact]
    public async Task NonPrimaryWanDown_AlertsOnThatWanOnlyAndAtWarning()
    {
        await RoundsAsync(RoundsToConfirm, failing: Wan2Targets, passing: WanTargets);

        _bus.Published.Should().ContainSingle();
        var evt = _bus.Published.Single();
        evt.EventType.Should().Be("monitoring.wan_outage");
        evt.Severity.Should().Be(AlertSeverity.Warning);
        evt.DeviceId.Should().Be("wan2");
        evt.Title.Should().StartWith("Internet down on Beta Cable WAN2");
    }

    #endregion

    #region Site rollup

    [Fact]
    public async Task EveryWanDownTogether_PublishesOneSiteRollupInsteadOfPerWanOutages()
    {
        await RoundsAsync(RoundsToConfirm, failing: [.. WanTargets, .. Wan2Targets], passing: NoTargets);

        _bus.Published.Should().ContainSingle();
        var evt = _bus.Published.Single();
        evt.EventType.Should().Be("monitoring.wan_outage");
        evt.Severity.Should().Be(AlertSeverity.Critical);
        evt.DeviceId.Should().Be("all-wans");
        _bus.Published.Should().NotContain(e => e.DeviceId == "wan" || e.DeviceId == "wan2");
    }

    /// <summary>
    /// The rollup's premise is that every WAN is out. The moment one comes back the picture goes
    /// back to per-WAN: the rollup closes and the WAN still dark opens its own alert.
    /// </summary>
    [Fact]
    public async Task OneWanRecoveringUnderTheRollup_ClosesItAndOpensTheWanStillDown()
    {
        await RoundsAsync(RoundsToConfirm, failing: [.. WanTargets, .. Wan2Targets], passing: NoTargets);
        await RoundsAsync(RoundsToConfirm, failing: Wan2Targets, passing: WanTargets);

        _bus.Published.Should().HaveCount(3);
        _bus.Published[0].DeviceId.Should().Be("all-wans");
        _bus.Published.Single(e => e.EventType == "monitoring.wan_recovered")
            .DeviceId.Should().Be("wan");
        _bus.Published.Skip(1).Single(e => e.EventType == "monitoring.wan_outage")
            .DeviceId.Should().Be("wan2");
    }

    #endregion

    #region What the WAN alerts must not change

    [Theory]
    [InlineData(MonitoringTargetType.Fabric)]
    [InlineData(MonitoringTargetType.Custom)]
    public async Task TargetOutsideTheWanCategories_StillPublishesPerTarget(MonitoringTargetType type)
    {
        var target = Target("lan-switch", "Switch 1", "192.0.2.10", type, deviceMac: "aabbccddeeff");

        await RoundsAsync(3, failing: [target], passing: NoTargets);

        _bus.Published.Should().ContainSingle();
        var evt = _bus.Published.Single();
        evt.EventType.Should().Be("monitoring.target_offline");
        evt.Severity.Should().Be(AlertSeverity.Warning);
        evt.Title.Should().Be("Switch 1 is offline");
        evt.DeviceId.Should().Be("aabbccddeeff");
    }

    /// <summary>
    /// Under load balancing every WAN carries live sessions, so a backup going dark is a real
    /// service loss rather than lost redundancy - it grades the same as the primary would.
    /// </summary>
    [Fact]
    public async Task NonPrimaryWanDownOnALoadBalancingSite_IsCritical()
    {
        var evaluator = BuildEvaluator(BuildContext(loadBalances: true));

        for (var round = 0; round < RoundsToConfirm; round++)
        {
            foreach (var target in Wan2Targets)
                await evaluator.EvaluateAsync(target, Probe(target, success: false));
            foreach (var target in WanTargets)
                await evaluator.EvaluateAsync(target, Probe(target, success: true));
            _time.Advance(TimeSpan.FromSeconds(SecondsPerPass));
        }

        var evt = _bus.Published.Should().ContainSingle().Subject;
        evt.EventType.Should().Be("monitoring.wan_outage");
        evt.DeviceId.Should().Be("wan2");
        evt.Severity.Should().Be(AlertSeverity.Critical);
    }

    #endregion

    #region Silences

    /// <summary>
    /// A verdict that does not hold long enough is not an outage. Probes run faster than passes,
    /// so a WAN can go dark and come back inside a couple of evaluation windows - which is what
    /// the confirmation count exists to swallow.
    /// </summary>
    [Fact]
    public async Task VerdictThatDoesNotHoldLongEnough_PublishesNothing()
    {
        // First failed probe: the pass it triggers has nothing recorded to judge yet.
        await ProbeAsync(WanTargets, success: false);

        // Second failed probe puts every target over the failing threshold, and the pass that
        // comes with it reaches a Total verdict - one confirming pass, one short of opening.
        _time.Advance(TimeSpan.FromSeconds(SecondsPerPass));
        await ProbeAsync(WanTargets, success: false);

        // Back up before the next pass. A success resets the failure count immediately (the
        // targets never reached the per-target offline threshold), so that pass sees a healthy
        // WAN and the pending Total never gets its second confirmation.
        _time.Advance(TimeSpan.FromSeconds(SecondsPerPass));
        await ProbeAsync(WanTargets, success: true);

        _bus.Published.Should().BeEmpty();
    }

    /// <summary>
    /// Targets that stop reporting say nothing about the WAN: a monitoring gap (agent gone,
    /// collection stopped) must not confirm an outage out of stale states.
    /// </summary>
    [Fact]
    public async Task WanWhoseTargetsStopReporting_PublishesNothing()
    {
        // One confirming pass on a failing WAN, then wan stops reporting entirely.
        await ProbeAsync(WanTargets, success: false);
        _time.Advance(TimeSpan.FromSeconds(SecondsPerPass));
        await ProbeAsync(WanTargets, success: false);

        // The other WAN keeps reporting, so passes keep running with wan's states long stale.
        _time.Advance(TimeSpan.FromMinutes(10));
        await RoundsAsync(RoundsToConfirm, failing: NoTargets, passing: Wan2Targets);

        _bus.Published.Should().BeEmpty();
    }

    #endregion

    #region Harness

    private async Task RoundsAsync(int rounds, IReadOnlyList<MonitoringTarget> failing,
        IReadOnlyList<MonitoringTarget> passing)
    {
        for (var round = 0; round < rounds; round++)
        {
            await ProbeAsync(failing, success: false);
            await ProbeAsync(passing, success: true);
            _time.Advance(TimeSpan.FromSeconds(SecondsPerPass));
        }
    }

    private async Task ProbeAsync(IReadOnlyList<MonitoringTarget> targets, bool success)
    {
        foreach (var target in targets)
            await _evaluator.EvaluateAsync(target, Probe(target, success));
    }

    private PingProbeResult Probe(MonitoringTarget target, bool success) => new()
    {
        Target = new ProbeTarget(target.Address, ProbeMode.Icmp),
        Vantage = ProbeVantage.Server,
        Sent = 10,
        Received = success ? 10 : 0,
        Timestamp = _time.GetUtcNow().UtcDateTime,
        RttAvgMs = success ? 12.5 : (double?)null
    };

    private static MonitoringTarget Target(string targetId, string name, string address,
        MonitoringTargetType type, string? wanInterface = null, int? asnNumber = null,
        string? asnName = null, string? deviceMac = null) => new()
        {
            TargetId = targetId,
            Name = name,
            Address = address,
            TargetType = type,
            WanInterface = wanInterface,
            AsnNumber = asnNumber,
            AsnName = asnName,
            DeviceMac = deviceMac
        };

    /// <summary>
    /// Two WANs with a first hop, a transit hop and two destinations each. resolver-a sits behind
    /// the monitored transit; resolver-b reaches the internet another way, so a pair of dark
    /// destinations has no shared branch to be named after.
    /// </summary>
    private static WanOutageContext BuildContext(bool loadBalances = false) => new(
        PrimaryWanKey: "wan",
        Wans: new Dictionary<string, WanOutageWanInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["wan"] = new("wan", "Acme Fiber WAN1", TreatAsPrimary: true, CarriesTraffic: true, ConsoleUp: null),
            ["wan2"] = new("wan2", "Beta Cable WAN2", TreatAsPrimary: false,
                CarriesTraffic: loadBalances, ConsoleUp: null)
        },
        HopsByTargetId: new Dictionary<string, WanOutageHopInfo>
        {
            ["wan-access"] = Hop(1),
            ["wan-transit"] = Hop(3, "192.0.2.1"),
            ["wan-resolver-a"] = Hop(6, "192.0.2.1", "198.51.100.1"),
            ["wan-resolver-b"] = Hop(6, "192.0.2.1"),
            ["wan2-access"] = Hop(1),
            ["wan2-transit"] = Hop(3, "192.0.2.65"),
            ["wan2-resolver-a"] = Hop(6, "192.0.2.65", "198.51.100.65"),
            ["wan2-resolver-b"] = Hop(6, "192.0.2.65")
        },
        AccessNeighborIpByWan: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["wan"] = "192.0.2.1",
            ["wan2"] = "192.0.2.65"
        });

    private static WanOutageHopInfo Hop(int depth, params string[] ancestors) =>
        new(depth, new HashSet<string>(ancestors, StringComparer.OrdinalIgnoreCase));

    private sealed class FakeContextSource : WanOutageContextSource
    {
        private readonly WanOutageContext _context;

        public FakeContextSource(WanOutageContext context)
            : base(null!, null!, NullLogger<WanOutageContextSource>.Instance) => _context = context;

        internal override Task<WanOutageContext> LoadAsync(string siteSlug,
            IReadOnlyCollection<string> wanKeysInUse, CancellationToken ct = default) =>
            Task.FromResult(_context);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public FakeTimeProvider(DateTime start) => _utcNow = new DateTimeOffset(start);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan by) => _utcNow = _utcNow.Add(by);
    }

    private sealed class CapturingAlertEventBus : IAlertEventBus
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

    #endregion
}
