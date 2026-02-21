using FluentAssertions;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;
using Xunit;

namespace NetworkOptimizer.Alerts.Tests;

public class AlertRuleEvaluatorTests
{
    private readonly AlertCooldownTracker _cooldownTracker = new();
    private readonly AlertRuleEvaluator _evaluator;

    public AlertRuleEvaluatorTests()
    {
        _evaluator = new AlertRuleEvaluator(_cooldownTracker);
    }

    private static AlertEvent CreateTestEvent(
        string eventType = "audit.score_dropped",
        AlertSeverity severity = AlertSeverity.Warning,
        string source = "audit",
        string? deviceId = null,
        string? deviceIp = null)
    {
        return new AlertEvent
        {
            EventType = eventType,
            Severity = severity,
            Source = source,
            Title = "Test alert",
            DeviceId = deviceId,
            DeviceIp = deviceIp
        };
    }

    private static AlertRule CreateTestRule(
        int id = 1,
        string eventTypePattern = "*",
        AlertSeverity minSeverity = AlertSeverity.Info,
        string? source = null,
        int cooldownSeconds = 0,
        bool isEnabled = true,
        bool digestOnly = false,
        string? targetDevices = null)
    {
        return new AlertRule
        {
            Id = id,
            Name = $"Test Rule {id}",
            IsEnabled = isEnabled,
            EventTypePattern = eventTypePattern,
            MinSeverity = minSeverity,
            Source = source ?? string.Empty,
            CooldownSeconds = cooldownSeconds,
            DigestOnly = digestOnly,
            TargetDevices = targetDevices
        };
    }

    #region Pattern Matching

    [Fact]
    public void Evaluate_WildcardPattern_MatchesAll()
    {
        var evt = CreateTestEvent("audit.score_dropped");
        var rules = new List<AlertRule> { CreateTestRule(eventTypePattern: "*") };

        var matches = _evaluator.Evaluate(evt, rules);

        matches.Should().HaveCount(1);
    }

    [Fact]
    public void Evaluate_EmptyPattern_MatchesAll()
    {
        var evt = CreateTestEvent("audit.score_dropped");
        var rules = new List<AlertRule> { CreateTestRule(eventTypePattern: "") };

        var matches = _evaluator.Evaluate(evt, rules);

        matches.Should().HaveCount(1);
    }

    [Fact]
    public void Evaluate_ExactMatch_Matches()
    {
        var evt = CreateTestEvent("audit.score_dropped");
        var rules = new List<AlertRule> { CreateTestRule(eventTypePattern: "audit.score_dropped") };

        var matches = _evaluator.Evaluate(evt, rules);

        matches.Should().HaveCount(1);
    }

    [Fact]
    public void Evaluate_ExactMatch_CaseInsensitive()
    {
        var evt = CreateTestEvent("Audit.Score_Dropped");
        var rules = new List<AlertRule> { CreateTestRule(eventTypePattern: "audit.score_dropped") };

        var matches = _evaluator.Evaluate(evt, rules);

        matches.Should().HaveCount(1);
    }

    [Fact]
    public void Evaluate_ExactMatch_NoMatch()
    {
        var evt = CreateTestEvent("device.offline");
        var rules = new List<AlertRule> { CreateTestRule(eventTypePattern: "audit.score_dropped") };

        var matches = _evaluator.Evaluate(evt, rules);

        matches.Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_PrefixWildcard_MatchesSamePrefix()
    {
        var evt = CreateTestEvent("audit.score_dropped");
        var rules = new List<AlertRule> { CreateTestRule(eventTypePattern: "audit.*") };

        var matches = _evaluator.Evaluate(evt, rules);

        matches.Should().HaveCount(1);
    }

    [Fact]
    public void Evaluate_PrefixWildcard_MatchesMultipleSubTypes()
    {
        var rules = new List<AlertRule> { CreateTestRule(eventTypePattern: "audit.*") };

        _evaluator.Evaluate(CreateTestEvent("audit.score_dropped"), rules).Should().HaveCount(1);
        _evaluator.Evaluate(CreateTestEvent("audit.new_critical_finding"), rules).Should().HaveCount(1);
        _evaluator.Evaluate(CreateTestEvent("audit.completed"), rules).Should().HaveCount(1);
    }

    [Fact]
    public void Evaluate_PrefixWildcard_NoMatchDifferentPrefix()
    {
        var evt = CreateTestEvent("device.offline");
        var rules = new List<AlertRule> { CreateTestRule(eventTypePattern: "audit.*") };

        var matches = _evaluator.Evaluate(evt, rules);

        matches.Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_PrefixWildcard_NoMatchPartialPrefix()
    {
        // "audit" without a dot separator should not match "audit.*"
        var evt = CreateTestEvent("auditing.done");
        var rules = new List<AlertRule> { CreateTestRule(eventTypePattern: "audit.*") };

        var matches = _evaluator.Evaluate(evt, rules);

        matches.Should().BeEmpty();
    }

    #endregion

    #region Severity Filtering

    [Fact]
    public void Evaluate_EventMeetsSeverity_Matches()
    {
        var evt = CreateTestEvent(severity: AlertSeverity.Error);
        var rules = new List<AlertRule> { CreateTestRule(minSeverity: AlertSeverity.Warning) };

        var matches = _evaluator.Evaluate(evt, rules);

        matches.Should().HaveCount(1);
    }

    [Fact]
    public void Evaluate_EventBelowSeverity_NoMatch()
    {
        var evt = CreateTestEvent(severity: AlertSeverity.Info);
        var rules = new List<AlertRule> { CreateTestRule(minSeverity: AlertSeverity.Warning) };

        var matches = _evaluator.Evaluate(evt, rules);

        matches.Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_EventEqualsSeverity_Matches()
    {
        var evt = CreateTestEvent(severity: AlertSeverity.Warning);
        var rules = new List<AlertRule> { CreateTestRule(minSeverity: AlertSeverity.Warning) };

        var matches = _evaluator.Evaluate(evt, rules);

        matches.Should().HaveCount(1);
    }

    #endregion

    #region Source Filtering

    [Fact]
    public void Evaluate_SourceMatches_Matches()
    {
        var evt = CreateTestEvent(source: "audit");
        var rules = new List<AlertRule> { CreateTestRule(source: "audit") };

        var matches = _evaluator.Evaluate(evt, rules);

        matches.Should().HaveCount(1);
    }

    [Fact]
    public void Evaluate_SourceDoesNotMatch_NoMatch()
    {
        var evt = CreateTestEvent(source: "speedtest");
        var rules = new List<AlertRule> { CreateTestRule(source: "audit") };

        var matches = _evaluator.Evaluate(evt, rules);

        matches.Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_EmptySourceFilter_MatchesAll()
    {
        var evt = CreateTestEvent(source: "anything");
        var rules = new List<AlertRule> { CreateTestRule(source: "") };

        var matches = _evaluator.Evaluate(evt, rules);

        matches.Should().HaveCount(1);
    }

    [Fact]
    public void Evaluate_SourceComparison_CaseInsensitive()
    {
        var evt = CreateTestEvent(source: "Audit");
        var rules = new List<AlertRule> { CreateTestRule(source: "audit") };

        var matches = _evaluator.Evaluate(evt, rules);

        matches.Should().HaveCount(1);
    }

    #endregion

    #region Target Device Filtering

    [Fact]
    public void Evaluate_NoTargetDevices_MatchesAll()
    {
        var evt = CreateTestEvent(deviceId: "aa:bb:cc:dd:ee:ff", deviceIp: "192.0.2.1");
        var rules = new List<AlertRule> { CreateTestRule(targetDevices: null) };

        var matches = _evaluator.Evaluate(evt, rules);

        matches.Should().HaveCount(1);
    }

    [Fact]
    public void Evaluate_DeviceIdInTargetList_Matches()
    {
        var evt = CreateTestEvent(deviceId: "aa:bb:cc:dd:ee:ff");
        var rules = new List<AlertRule> { CreateTestRule(targetDevices: "aa:bb:cc:dd:ee:ff,11:22:33:44:55:66") };

        var matches = _evaluator.Evaluate(evt, rules);

        matches.Should().HaveCount(1);
    }

    [Fact]
    public void Evaluate_DeviceIpInTargetList_Matches()
    {
        var evt = CreateTestEvent(deviceIp: "192.0.2.1");
        var rules = new List<AlertRule> { CreateTestRule(targetDevices: "192.0.2.1,192.0.2.2") };

        var matches = _evaluator.Evaluate(evt, rules);

        matches.Should().HaveCount(1);
    }

    [Fact]
    public void Evaluate_DeviceNotInTargetList_NoMatch()
    {
        var evt = CreateTestEvent(deviceId: "aa:bb:cc:dd:ee:ff", deviceIp: "192.0.2.1");
        var rules = new List<AlertRule> { CreateTestRule(targetDevices: "11:22:33:44:55:66,192.0.2.99") };

        var matches = _evaluator.Evaluate(evt, rules);

        matches.Should().BeEmpty();
    }

    #endregion

    #region Disabled Rules

    [Fact]
    public void Evaluate_DisabledRule_Skipped()
    {
        var evt = CreateTestEvent();
        var rules = new List<AlertRule> { CreateTestRule(isEnabled: false) };

        var matches = _evaluator.Evaluate(evt, rules);

        matches.Should().BeEmpty();
    }

    #endregion

    #region Cooldown

    [Fact]
    public void Evaluate_WithinCooldown_Suppressed()
    {
        var evt = CreateTestEvent(deviceId: "device1");
        var rule = CreateTestRule(id: 1, cooldownSeconds: 300);
        var rules = new List<AlertRule> { rule };

        // First evaluation should match
        var first = _evaluator.Evaluate(evt, rules);
        first.Should().HaveCount(1);

        // Record the fire
        _evaluator.RecordFired(rule, evt);

        // Second evaluation should be suppressed (within cooldown)
        var second = _evaluator.Evaluate(evt, rules);
        second.Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_NoCooldown_AlwaysMatches()
    {
        var evt = CreateTestEvent(deviceId: "device1");
        var rule = CreateTestRule(id: 2, cooldownSeconds: 0);
        var rules = new List<AlertRule> { rule };

        _evaluator.Evaluate(evt, rules).Should().HaveCount(1);
        _evaluator.RecordFired(rule, evt);
        _evaluator.Evaluate(evt, rules).Should().HaveCount(1);
    }

    [Fact]
    public void Evaluate_DifferentDevices_IndependentCooldown()
    {
        var rule = CreateTestRule(id: 3, cooldownSeconds: 300);
        var rules = new List<AlertRule> { rule };

        var evt1 = CreateTestEvent(deviceId: "device1");
        var evt2 = CreateTestEvent(deviceId: "device2");

        // Fire for device1
        _evaluator.Evaluate(evt1, rules).Should().HaveCount(1);
        _evaluator.RecordFired(rule, evt1);

        // Device2 should still match (independent cooldown)
        _evaluator.Evaluate(evt2, rules).Should().HaveCount(1);

        // Device1 should be suppressed
        _evaluator.Evaluate(evt1, rules).Should().BeEmpty();
    }

    #endregion

    #region Multiple Rules

    [Fact]
    public void Evaluate_MultipleMatchingRules_ReturnsAll()
    {
        var evt = CreateTestEvent("audit.score_dropped", AlertSeverity.Critical);
        var rules = new List<AlertRule>
        {
            CreateTestRule(id: 1, eventTypePattern: "audit.*"),
            CreateTestRule(id: 2, eventTypePattern: "*", minSeverity: AlertSeverity.Critical),
            CreateTestRule(id: 3, eventTypePattern: "device.*") // Won't match
        };

        var matches = _evaluator.Evaluate(evt, rules);

        matches.Should().HaveCount(2);
        matches.Select(r => r.Id).Should().Contain(new[] { 1, 2 });
    }

    #endregion

    #region Static Pattern Matching

    [Theory]
    [InlineData("audit.score_dropped", "*", true)]
    [InlineData("audit.score_dropped", "", true)]
    [InlineData("audit.score_dropped", "audit.score_dropped", true)]
    [InlineData("audit.score_dropped", "audit.*", true)]
    [InlineData("audit.score_dropped", "device.*", false)]
    [InlineData("audit.score_dropped", "audit.new_finding", false)]
    [InlineData("device.offline", "device.*", true)]
    [InlineData("device", "device.*", false)] // No dot after prefix
    public void MatchesEventType_ReturnsExpected(string eventType, string pattern, bool expected)
    {
        AlertRuleEvaluator.MatchesEventType(eventType, pattern).Should().Be(expected);
    }

    #endregion
}
