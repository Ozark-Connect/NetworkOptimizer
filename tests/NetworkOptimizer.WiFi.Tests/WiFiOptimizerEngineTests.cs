using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.WiFi.Rules;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class WiFiOptimizerEngineTests
{
    private sealed class BareRule : IWiFiOptimizerRule
    {
        public string RuleId => "WIFI-BARE-001";
        public HealthIssue? Evaluate(WiFiOptimizerContext context) =>
            new() { Title = "Bare", AffectedEntity = "AP-One (12)" };
    }

    private sealed class KeyedRule : IWiFiOptimizerRule
    {
        public string RuleId => "WIFI-KEYED-001";
        public HealthIssue? Evaluate(WiFiOptimizerContext context) =>
            new() { Title = "Keyed", Key = "WIFI-KEYED-001|x", Class = HealthIssueClass.Advisory };
    }

    private static WiFiOptimizerContext Context() => new()
    {
        AccessPoints = [], Clients = [], Wlans = [], Networks = [], LegacyClients = [], SteerableClients = []
    };

    [Fact]
    public void A_rule_that_sets_no_key_or_class_gets_a_subject_key_and_reads_as_measured()
    {
        var engine = new WiFiOptimizerEngine(new IWiFiOptimizerRule[] { new BareRule() }, NullLogger<WiFiOptimizerEngine>.Instance);

        var issue = engine.EvaluateRules(Context()).Single();

        issue.RuleId.Should().Be("WIFI-BARE-001");
        issue.Key.Should().Be("WIFI-BARE-001|ap-one (12)");
        issue.Class.Should().Be(HealthIssueClass.Measured);
    }

    [Fact]
    public void A_rule_that_sets_key_and_class_keeps_them()
    {
        var engine = new WiFiOptimizerEngine(new IWiFiOptimizerRule[] { new KeyedRule() }, NullLogger<WiFiOptimizerEngine>.Instance);

        var issue = engine.EvaluateRules(Context()).Single();

        issue.Key.Should().Be("WIFI-KEYED-001|x");
        issue.Class.Should().Be(HealthIssueClass.Advisory);
        issue.RuleId.Should().Be("WIFI-KEYED-001");
    }
}
