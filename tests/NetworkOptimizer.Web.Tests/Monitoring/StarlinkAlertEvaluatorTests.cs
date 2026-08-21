using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// The Starlink alert rules, driven the way the dish poll drives them: one <see cref="StarlinkStats"/>
/// reading at a time, with time advanced between them so the sustain windows are exercised rather
/// than waited out.
///
/// <para>
/// The load-bearing cases are the ones that must stay SILENT. Every "problem" field on the
/// reference dish is populated while nothing is wrong - it reports install_pending continuously,
/// fails its hardware self-test continuously, sits at a permanent rate limit, and points a steady
/// 3.69 degrees off desired - so a rule written as "the field has a value" would fire on day one
/// and never stop. Each of those has a test here that asserts nothing is published.
/// </para>
/// </summary>
public class StarlinkAlertEvaluatorTests
{
    private const int DishId = 1;
    private const string DishName = "Starlink Roof";

    private static readonly DateTime Start = new(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeTimeProvider _time = new(Start);
    private readonly CapturingBus _bus = new();
    private readonly StarlinkAlertEvaluator _evaluator;

    public StarlinkAlertEvaluatorTests()
    {
        _evaluator = new StarlinkAlertEvaluator(
            _bus, NullLogger<StarlinkAlertEvaluator>.Instance, timeProvider: _time);
    }

    /// <summary>
    /// A reading from a dish with nothing wrong, matching what the reference dish actually
    /// reports over 30 days: a benign standing alert code and no other, a self-test that has
    /// always failed, a permanent rate limit on both directions, mobility class Mobile on a
    /// bolted-down dish, a constant gigabit Ethernet link, a median 0.06% obstructed, and the
    /// median 0.70 degrees of attitude uncertainty a healthy dish carries.
    /// </summary>
    private static StarlinkStats Healthy() => new()
    {
        ActiveAlerts = ["install_pending"],
        HardwareSelfTest = "Failed",
        DisablementCode = "Okay",
        DownlinkRestrictedReason = "LowSpeedPolicyLimit",
        UplinkRestrictedReason = "PolicyLimit",
        ClassOfService = "Consumer",
        MobilityClass = "Mobile",
        SoftwareUpdateState = "Idle",
        EthSpeedMbps = 1000,
        FractionObstructed = 0.0006,
        IsSnrPersistentlyLow = false,
        AttitudeUncertaintyDeg = 0.70,
    };

    private ValueTask Feed(StarlinkStats stats, double? alignmentOffsetDeg = null,
        double? baselineDeg = null, int? ethCapableMbps = 1000, string? wanLabel = null) =>
        _evaluator.EvaluateAsync(DishId, DishName, stats,
            alignmentOffsetDeg, baselineDeg, ethCapableMbps, wanLabel);

    private List<AlertEvent> Of(string eventType) =>
        _bus.Published.Where(e => e.EventType == eventType).ToList();

    private List<AlertEvent> RecoveriesOf(string recoveredType) =>
        _bus.Published
            .Where(e => e.EventType == "starlink.recovered"
                        && e.Context.TryGetValue("recovered_type", out var t) && t == recoveredType)
            .ToList();

    // --- The silence cases ---------------------------------------------------------------

    [Fact]
    public async Task HealthyDish_PublishesNothing()
    {
        for (var i = 0; i < 200; i++)
        {
            await Feed(Healthy(), alignmentOffsetDeg: 3.69, baselineDeg: 3.69);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        _bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task PermanentlyRestrictedSubscription_NeverAlerts()
    {
        // The reference dish is rate limited on both directions continuously and always has been.
        for (var i = 0; i < 10; i++)
        {
            await Feed(Healthy());
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        Of("starlink.service_restricted").Should().BeEmpty();
    }

    [Fact]
    public async Task SelfTestThatHasAlwaysFailed_IsNotAFault()
    {
        for (var i = 0; i < 10; i++)
        {
            await Feed(Healthy());
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        Of("starlink.dish_alert").Should().BeEmpty();
    }

    [Fact]
    public async Task DishSittingAtASteadyNonZeroOffset_NeverAlerts()
    {
        // 30 days of the reference dish: median 3.69, with the measured p1/p99 spread and the two
        // isolated outliers that are exactly why the current value is a median and not a sample.
        double[] wander = [3.69, 3.42, 3.96, 3.53, 3.84, 2.04, 3.69, 4.64, 3.61, 3.75];

        for (var i = 0; i < 300; i++)
        {
            await Feed(Healthy(), alignmentOffsetDeg: wander[i % wander.Length], baselineDeg: 3.69);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        Of("starlink.alignment_drift").Should().BeEmpty();
    }

    [Fact]
    public async Task BenignDishAlertCodes_AreIgnored()
    {
        var stats = Healthy();
        stats.ActiveAlerts = ["install_pending", "is_heating", "is_power_save_idle", "roaming", "obstruction_map_reset"];

        await Feed(stats);

        Of("starlink.dish_alert").Should().BeEmpty();
    }

    // --- starlink.dish_alert -------------------------------------------------------------

    [Fact]
    public async Task NonBenignDishAlertCode_PublishesOnceAndCarriesTheCodeVerbatim()
    {
        var stats = Healthy();
        stats.ActiveAlerts = ["install_pending", "thermal_shutdown"];

        await Feed(stats);
        _time.Advance(TimeSpan.FromMinutes(1));
        await Feed(stats);

        var events = Of("starlink.dish_alert");
        events.Should().ContainSingle("a standing set of codes is one open alert, not one per poll");
        events[0].Severity.Should().Be(AlertSeverity.Warning);
        events[0].Source.Should().Be("starlink");
        events[0].Message.Should().Contain("thermal_shutdown").And.NotContain("install_pending");
        events[0].Context["dish_alerts"].Should().Be("thermal_shutdown");
    }

    [Fact]
    public async Task ANewCodeOnTopOfAnOpenDishAlert_Republishes()
    {
        var stats = Healthy();
        stats.ActiveAlerts = ["thermal_shutdown"];
        await Feed(stats);

        _time.Advance(TimeSpan.FromMinutes(1));
        var worse = Healthy();
        worse.ActiveAlerts = ["thermal_shutdown", "motors_stuck"];
        await Feed(worse);

        Of("starlink.dish_alert").Should().HaveCount(2);
    }

    [Fact]
    public async Task DisablementCodeOtherThanOkay_IsCritical()
    {
        var stats = Healthy();
        stats.DisablementCode = "NoActiveAccount";

        await Feed(stats);

        var events = Of("starlink.dish_alert");
        events.Should().ContainSingle();
        events[0].Severity.Should().Be(AlertSeverity.Critical);
        events[0].Message.Should().Contain("NoActiveAccount");
        events[0].Context["disablement_code"].Should().Be("NoActiveAccount");
    }

    [Fact]
    public async Task SelfTestGoingFromPassingToFailing_IsAFault()
    {
        var passing = Healthy();
        passing.HardwareSelfTest = "Passed";
        await Feed(passing);

        _time.Advance(TimeSpan.FromMinutes(1));
        await Feed(Healthy()); // back to "Failed"

        var events = Of("starlink.dish_alert");
        events.Should().ContainSingle();
        events[0].Message.Should().Contain("self-test");
    }

    [Fact]
    public async Task DishAlertClearing_PublishesRecoveryAndReArms()
    {
        var faulted = Healthy();
        faulted.ActiveAlerts = ["thermal_shutdown"];
        await Feed(faulted);

        _time.Advance(TimeSpan.FromMinutes(1));
        await Feed(Healthy());

        _time.Advance(TimeSpan.FromMinutes(1));
        await Feed(faulted);

        RecoveriesOf("starlink.dish_alert").Should().ContainSingle();
        Of("starlink.dish_alert").Should().HaveCount(2, "the condition cleared, so it can raise again");
    }

    // --- starlink.obstructed -------------------------------------------------------------

    [Fact]
    public async Task ObstructionBelowTheSustainWindow_DoesNotAlert()
    {
        var stats = Healthy();
        stats.FractionObstructed = 0.05;

        for (var i = 0; i < 10; i++)
        {
            await Feed(stats);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        Of("starlink.obstructed").Should().BeEmpty("obstruction is momentary by design");
    }

    [Fact]
    public async Task SustainedObstruction_AlertsOnce()
    {
        var stats = Healthy();
        stats.FractionObstructed = 0.05;

        for (var i = 0; i < 40; i++)
        {
            await Feed(stats);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        var events = Of("starlink.obstructed");
        events.Should().ContainSingle();
        events[0].Severity.Should().Be(AlertSeverity.Warning);
        events[0].MetricValue.Should().Be(0.05);
    }

    [Fact]
    public async Task ObstructionPastTheCriticalBar_PublishesCritical()
    {
        var stats = Healthy();
        stats.FractionObstructed = 0.2;

        for (var i = 0; i < 40; i++)
        {
            await Feed(stats);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        Of("starlink.obstructed").Should().ContainSingle()
            .Which.Severity.Should().Be(AlertSeverity.Critical);
    }

    [Fact]
    public async Task ObstructionEscalatingFromPoorToCritical_Republishes()
    {
        var poor = Healthy();
        poor.FractionObstructed = 0.05;
        for (var i = 0; i < 20; i++)
        {
            await Feed(poor);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        var critical = Healthy();
        critical.FractionObstructed = 0.2;
        await Feed(critical);

        var events = Of("starlink.obstructed");
        events.Should().HaveCount(2);
        events[0].Severity.Should().Be(AlertSeverity.Warning);
        events[1].Severity.Should().Be(AlertSeverity.Critical);
    }

    [Fact]
    public async Task PersistentlyLowSnr_AlertsAsObstruction()
    {
        var stats = Healthy();
        stats.IsSnrPersistentlyLow = true;

        for (var i = 0; i < 20; i++)
        {
            await Feed(stats);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        var evt = Of("starlink.obstructed").Should().ContainSingle().Subject;
        evt.Context["snr_persistently_low"].Should().Be("true");
        // The obstruction fraction is healthy here, so quoting it against the poor-obstruction
        // threshold would read as "0.0006 against 0.02" beside a message about low signal.
        evt.MetricValue.Should().BeNull();
        evt.ThresholdValue.Should().BeNull();
    }

    [Fact]
    public async Task ObstructionClearing_PublishesRecovery()
    {
        var stats = Healthy();
        stats.FractionObstructed = 0.05;
        for (var i = 0; i < 20; i++)
        {
            await Feed(stats);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        for (var i = 0; i < 20; i++)
        {
            await Feed(Healthy());
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        RecoveriesOf("starlink.obstructed").Should().ContainSingle();
    }

    // --- starlink.alignment_drift ---------------------------------------------------------

    [Fact]
    public async Task SustainedStepBeyondTwoDegrees_AlertsOnce()
    {
        await Settle(offset: 3.69, baseline: 3.69);

        for (var i = 0; i < 120; i++)
        {
            await Feed(Healthy(), alignmentOffsetDeg: 6.5, baselineDeg: 3.69);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        var events = Of("starlink.alignment_drift");
        events.Should().ContainSingle();
        events[0].Severity.Should().Be(AlertSeverity.Warning);
        events[0].ThresholdValue.Should().Be(2.0);
    }

    [Fact]
    public async Task StepBeyondTwoDegreesThatDoesNotHold_DoesNotAlert()
    {
        await Settle(offset: 3.69, baseline: 3.69);

        // Ten minutes of departure, well inside the 30 minute window.
        for (var i = 0; i < 10; i++)
        {
            await Feed(Healthy(), alignmentOffsetDeg: 6.5, baselineDeg: 3.69);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        Of("starlink.alignment_drift").Should().BeEmpty();
    }

    /// <summary>
    /// A healthy dish is nowhere near certain of its attitude - the reference dish runs p50 0.70,
    /// p95 1.49, p99 1.83 degrees of uncertainty over 30 days - so the gate must sit above that
    /// whole range. An earlier 1 degree bar gated out most healthy polls, and because a gated poll
    /// stalls the sustain, the drift alert could never hold its 30 minute window: the rule was
    /// dead rather than quiet. This pins the gate open across the real healthy distribution.
    /// </summary>
    [Theory]
    [InlineData(0.70)]
    [InlineData(1.49)]
    [InlineData(1.83)]
    [InlineData(2.71)]
    public async Task RealDriftAtHealthyAttitudeUncertainty_StillAlerts(double uncertaintyDeg)
    {
        var dish = Healthy();
        dish.AttitudeUncertaintyDeg = uncertaintyDeg;

        for (var i = 0; i < 60; i++)
        {
            await Feed(dish, alignmentOffsetDeg: 3.69, baselineDeg: 3.69);
            _time.Advance(TimeSpan.FromMinutes(1));
        }
        for (var i = 0; i < 120; i++)
        {
            await Feed(dish, alignmentOffsetDeg: 6.5, baselineDeg: 3.69);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        Of("starlink.alignment_drift").Should().ContainSingle();
    }

    [Fact]
    public async Task StepBeyondTwoDegreesWithHighAttitudeUncertainty_DoesNotAlert()
    {
        await Settle(offset: 3.69, baseline: 3.69);

        var confused = Healthy();
        confused.AttitudeUncertaintyDeg = 5;
        for (var i = 0; i < 120; i++)
        {
            await Feed(confused, alignmentOffsetDeg: 6.5, baselineDeg: 3.69);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        Of("starlink.alignment_drift").Should().BeEmpty(
            "above the uncertainty bar the dish does not know where it is pointing");
    }

    [Fact]
    public async Task DriftReturningToBaseline_ClosesTheAlert()
    {
        await Settle(offset: 3.69, baseline: 3.69);

        for (var i = 0; i < 120; i++)
        {
            await Feed(Healthy(), alignmentOffsetDeg: 6.5, baselineDeg: 3.69);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        Of("starlink.alignment_drift").Should().ContainSingle();

        for (var i = 0; i < 180; i++)
        {
            await Feed(Healthy(), alignmentOffsetDeg: 3.69, baselineDeg: 3.69);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        RecoveriesOf("starlink.alignment_drift").Should().ContainSingle();
    }

    /// <summary>
    /// A run of drift interrupted by polls that could not be judged must start over, not confirm
    /// on the far side of the gap as if it had held throughout.
    /// </summary>
    [Fact]
    public async Task DriftRunInterruptedByUnjudgeablePolls_StartsOver()
    {
        await Settle(offset: 3.69, baseline: 3.69);

        for (var i = 0; i < 40; i++)
        {
            await Feed(Healthy(), alignmentOffsetDeg: 6.5, baselineDeg: 3.69);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        // The dish stops reporting its geometry for a while, then comes straight back drifted.
        for (var i = 0; i < 5; i++)
        {
            await Feed(Healthy(), alignmentOffsetDeg: null, baselineDeg: 3.69);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        await Feed(Healthy(), alignmentOffsetDeg: 6.5, baselineDeg: 3.69);

        Of("starlink.alignment_drift").Should().BeEmpty();
    }

    [Fact]
    public async Task NoBaselineYet_LeavesTheDriftRuleDisabled()
    {
        for (var i = 0; i < 120; i++)
        {
            await Feed(Healthy(), alignmentOffsetDeg: 40, baselineDeg: null);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        Of("starlink.alignment_drift").Should().BeEmpty();
    }

    // --- starlink.eth_speed_degraded ------------------------------------------------------

    [Fact]
    public async Task EthernetNegotiatedBelowWhatTheDishReaches_AlertsOnce()
    {
        var stats = Healthy();
        stats.EthSpeedMbps = 100;

        for (var i = 0; i < 20; i++)
        {
            await Feed(stats, ethCapableMbps: 1000);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        var events = Of("starlink.eth_speed_degraded");
        events.Should().ContainSingle();
        events[0].MetricValue.Should().Be(100);
        events[0].ThresholdValue.Should().Be(1000);
    }

    [Fact]
    public async Task BriefEthernetRenegotiation_DoesNotAlert()
    {
        var stats = Healthy();
        stats.EthSpeedMbps = 100;

        await Feed(stats, ethCapableMbps: 1000);
        _time.Advance(TimeSpan.FromMinutes(1));
        await Feed(Healthy(), ethCapableMbps: 1000);

        Of("starlink.eth_speed_degraded").Should().BeEmpty();
    }

    [Fact]
    public async Task NoKnownCapableRate_LeavesTheEthernetRuleDisabled()
    {
        var stats = Healthy();
        stats.EthSpeedMbps = 100;

        for (var i = 0; i < 20; i++)
        {
            await Feed(stats, ethCapableMbps: null);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        Of("starlink.eth_speed_degraded").Should().BeEmpty();
    }

    [Fact]
    public async Task EthernetComingBackUpToSpeed_PublishesRecovery()
    {
        var slow = Healthy();
        slow.EthSpeedMbps = 100;
        for (var i = 0; i < 20; i++)
        {
            await Feed(slow, ethCapableMbps: 1000);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        for (var i = 0; i < 20; i++)
        {
            await Feed(Healthy(), ethCapableMbps: 1000);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        RecoveriesOf("starlink.eth_speed_degraded").Should().ContainSingle();
    }

    // --- starlink.outage_burst ------------------------------------------------------------

    [Fact]
    public async Task OutageSecondsPassingTheDailyBar_AlertsOnceWithTheCause()
    {
        for (var i = 0; i < 12; i++)
        {
            var stats = Healthy();
            stats.OutageSecondsDelta = 30;
            stats.LastOutageCause = "OBSTRUCTED";
            await Feed(stats);
            _time.Advance(TimeSpan.FromMinutes(10));
        }

        var events = Of("starlink.outage_burst");
        events.Should().ContainSingle();
        events[0].MetricValue.Should().Be(300, "it fires on the poll that crosses the bar, not on the last one fed");
        events[0].Message.Should().Contain("OBSTRUCTED");
    }

    [Fact]
    public async Task OutageSecondsUnderTheDailyBar_DoesNotAlert()
    {
        for (var i = 0; i < 9; i++)
        {
            var stats = Healthy();
            stats.OutageSecondsDelta = 30;
            await Feed(stats);
            _time.Advance(TimeSpan.FromMinutes(10));
        }

        Of("starlink.outage_burst").Should().BeEmpty();
    }

    [Fact]
    public async Task OutagesAgingOutOfTheWindow_PublishRecovery()
    {
        for (var i = 0; i < 12; i++)
        {
            var stats = Healthy();
            stats.OutageSecondsDelta = 30;
            await Feed(stats);
            _time.Advance(TimeSpan.FromMinutes(10));
        }

        Of("starlink.outage_burst").Should().ContainSingle();

        _time.Advance(TimeSpan.FromDays(2));
        await Feed(Healthy());

        RecoveriesOf("starlink.outage_burst").Should().ContainSingle();
    }

    // --- starlink.service_restricted -------------------------------------------------------

    [Fact]
    public async Task CrossingFromUnrestrictedIntoRestricted_PublishesInfo()
    {
        var free = Healthy();
        free.DownlinkRestrictedReason = "NoLimit";
        free.UplinkRestrictedReason = "NoLimit";
        await Feed(free);

        _time.Advance(TimeSpan.FromMinutes(1));
        await Feed(Healthy()); // permanently-restricted reference values

        var events = Of("starlink.service_restricted");
        events.Should().ContainSingle();
        events[0].Severity.Should().Be(AlertSeverity.Info);
        events[0].Context["dl_restricted_reason"].Should().Be("LowSpeedPolicyLimit");
    }

    [Fact]
    public async Task RestrictionLifting_PublishesRecovery()
    {
        var free = Healthy();
        free.DownlinkRestrictedReason = "NoLimit";
        free.UplinkRestrictedReason = "NoLimit";

        await Feed(free);
        _time.Advance(TimeSpan.FromMinutes(1));
        await Feed(Healthy());
        _time.Advance(TimeSpan.FromMinutes(1));
        await Feed(free);

        RecoveriesOf("starlink.service_restricted").Should().ContainSingle();
    }

    [Fact]
    public async Task RestrictionReportedInScreamingSnakeCase_ReadsTheSameValues()
    {
        var free = Healthy();
        free.DownlinkRestrictedReason = "NO_LIMIT";
        free.UplinkRestrictedReason = "NO_LIMIT";
        await Feed(free);

        _time.Advance(TimeSpan.FromMinutes(1));
        var limited = Healthy();
        limited.DownlinkRestrictedReason = "LOW_SPEED_POLICY_LIMIT";
        limited.UplinkRestrictedReason = "NO_LIMIT";
        await Feed(limited);

        Of("starlink.service_restricted").Should().ContainSingle();
    }

    // --- Labelling ------------------------------------------------------------------------

    [Fact]
    public async Task WithNoWanBinding_TheAlertNamesTheDish()
    {
        var stats = Healthy();
        stats.ActiveAlerts = ["thermal_shutdown"];

        await Feed(stats, wanLabel: null);

        var evt = Of("starlink.dish_alert").Should().ContainSingle().Subject;
        evt.Title.Should().StartWith(DishName);
        evt.DeviceName.Should().Be(DishName);
        evt.DeviceId.Should().Be("starlink:1");
        // Framed on the moment it fired, so the tab opens on the hour the alert is about.
        evt.SourceUrl.Should().StartWith("/monitoring?tab=starlink&at=");
        evt.SourceUrl.Should().EndWith("&starlink=1");
        evt.Context["dish_name"].Should().Be(DishName);
    }

    /// <summary>
    /// Dishes get called "Starlink Roof", "Roof Starlink", or just "Starlink", and the
    /// out-of-service sentence opens with the word itself - so the name is trimmed there and only
    /// there. Titles keep the full name: a title is often all that reaches a notification channel.
    /// </summary>
    [Theory]
    [InlineData("Starlink Roof", "Starlink has taken Roof out of service")]
    [InlineData("Roof Starlink", "Starlink has taken Roof out of service")]
    [InlineData("Starlink", "Starlink has taken the dish out of service")]
    [InlineData("Dishy McFlatface", "Starlink has taken Dishy McFlatface out of service")]
    public async Task OutOfServiceSentence_DoesNotSayStarlinkTwice(string dishName, string expected)
    {
        var stats = Healthy();
        stats.DisablementCode = "NoActiveAccount";

        await _evaluator.EvaluateAsync(DishId, dishName, stats);

        var evt = Of("starlink.dish_alert").Should().ContainSingle().Subject;
        evt.Message.Should().Contain(expected);
        evt.Title.Should().StartWith(dishName, "the title keeps the name whole");
    }

    [Fact]
    public async Task WithAWanBinding_TheAlertNamesTheWan()
    {
        var stats = Healthy();
        stats.ActiveAlerts = ["thermal_shutdown"];

        await Feed(stats, wanLabel: "Starlink WAN2");

        var evt = Of("starlink.dish_alert").Should().ContainSingle().Subject;
        evt.Title.Should().StartWith("Starlink WAN2");
        evt.Context["wan_label"].Should().Be("Starlink WAN2");
        evt.Context["dish_name"].Should().Be(DishName, "the dish is still identified in the context");
    }

    /// <summary>
    /// A dish on a WAN with nothing else watching it is the PRIMARY case, not an edge case: no
    /// vantage, no agent, no monitored targets, and the evaluator is fed by the dish poll alone.
    /// This test is that shape - nothing but readings goes in - and it still alerts.
    /// </summary>
    [Fact]
    public async Task DishOnAWanWithNoMonitoredTargets_AlertsNormally()
    {
        var stats = Healthy();
        stats.FractionObstructed = 0.05;

        for (var i = 0; i < 40; i++)
        {
            await Feed(stats, wanLabel: null);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        Of("starlink.obstructed").Should().ContainSingle();
    }

    // --- Fixtures -------------------------------------------------------------------------

    /// <summary>
    /// Fills the alignment sample window at a steady offset, so a following step change is
    /// measured against a settled median rather than against a half-empty window.
    /// </summary>
    private async Task Settle(double offset, double baseline)
    {
        for (var i = 0; i < 60; i++)
        {
            await Feed(Healthy(), alignmentOffsetDeg: offset, baselineDeg: baseline);
            _time.Advance(TimeSpan.FromMinutes(1));
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public FakeTimeProvider(DateTime start) => _utcNow = new DateTimeOffset(start);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan by) => _utcNow = _utcNow.Add(by);
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
