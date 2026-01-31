using FluentAssertions;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Analyzers;

public class FirewallRuleEvaluatorTests
{
    #region Evaluate Tests

    [Fact]
    public void Evaluate_NoMatchingRules_ReturnsNullEffectiveRule()
    {
        var rules = new List<FirewallRule>
        {
            CreateRule("Rule1", "ACCEPT", 100, "NETWORK", new List<string> { "net-a" })
        };

        var result = FirewallRuleEvaluator.Evaluate(rules, r => r.SourceNetworkIds?.Contains("net-b") == true);

        result.EffectiveRule.Should().BeNull();
        result.IsBlocked.Should().BeFalse();
        result.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_SingleBlockRule_ReturnsBlocked()
    {
        var rules = new List<FirewallRule>
        {
            CreateRule("Block Rule", "DROP", 100, "NETWORK", new List<string> { "net-a" })
        };

        var result = FirewallRuleEvaluator.Evaluate(rules, r => r.SourceNetworkIds?.Contains("net-a") == true);

        result.EffectiveRule.Should().NotBeNull();
        result.EffectiveRule!.Name.Should().Be("Block Rule");
        result.IsBlocked.Should().BeTrue();
        result.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_SingleAllowRule_ReturnsAllowed()
    {
        var rules = new List<FirewallRule>
        {
            CreateRule("Allow Rule", "ACCEPT", 100, "NETWORK", new List<string> { "net-a" })
        };

        var result = FirewallRuleEvaluator.Evaluate(rules, r => r.SourceNetworkIds?.Contains("net-a") == true);

        result.EffectiveRule.Should().NotBeNull();
        result.EffectiveRule!.Name.Should().Be("Allow Rule");
        result.IsBlocked.Should().BeFalse();
        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_AllowRuleBeforeBlockRule_ReturnsAllowedWithEclipsedBlock()
    {
        var rules = new List<FirewallRule>
        {
            CreateRule("Allow Rule", "ACCEPT", 100, "ANY", null),
            CreateRule("Block Rule", "DROP", 200, "ANY", null)
        };

        var result = FirewallRuleEvaluator.Evaluate(rules, r => true);

        result.EffectiveRule!.Name.Should().Be("Allow Rule");
        result.IsAllowed.Should().BeTrue();
        result.IsBlocked.Should().BeFalse();
        result.BlockRuleEclipsed.Should().BeTrue();
        result.EclipsedBlockRule!.Name.Should().Be("Block Rule");
    }

    [Fact]
    public void Evaluate_BlockRuleBeforeAllowRule_ReturnsBlockedWithEclipsedAllow()
    {
        var rules = new List<FirewallRule>
        {
            CreateRule("Block Rule", "DROP", 100, "ANY", null),
            CreateRule("Allow Rule", "ACCEPT", 200, "ANY", null)
        };

        var result = FirewallRuleEvaluator.Evaluate(rules, r => true);

        result.EffectiveRule!.Name.Should().Be("Block Rule");
        result.IsBlocked.Should().BeTrue();
        result.IsAllowed.Should().BeFalse();
        result.AllowRuleEclipsed.Should().BeTrue();
        result.EclipsedAllowRule!.Name.Should().Be("Allow Rule");
    }

    [Fact]
    public void Evaluate_RulesNotInIndexOrder_SortsByIndex()
    {
        // Rules added in wrong order - should still sort by index
        var rules = new List<FirewallRule>
        {
            CreateRule("Block Rule", "DROP", 200, "ANY", null),
            CreateRule("Allow Rule", "ACCEPT", 100, "ANY", null)
        };

        var result = FirewallRuleEvaluator.Evaluate(rules, r => true);

        result.EffectiveRule!.Name.Should().Be("Allow Rule");
        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_DisabledRulesIgnored()
    {
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "1", Name = "Disabled Allow", Action = "ACCEPT", Index = 100,
                Enabled = false, SourceMatchingTarget = "ANY"
            },
            CreateRule("Enabled Block", "DROP", 200, "ANY", null)
        };

        var result = FirewallRuleEvaluator.Evaluate(rules, r => true);

        result.EffectiveRule!.Name.Should().Be("Enabled Block");
        result.IsBlocked.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_BlockRuleWithOnlyInvalidState_NotConsideredBlocking()
    {
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "1", Name = "Block Invalid Only", Action = "DROP", Index = 100,
                Enabled = true, SourceMatchingTarget = "ANY",
                ConnectionStateType = "CUSTOM",
                ConnectionStates = new List<string> { "INVALID" }
            },
            CreateRule("Allow Rule", "ACCEPT", 200, "ANY", null)
        };

        var result = FirewallRuleEvaluator.Evaluate(rules, r => true);

        // Block Invalid Only doesn't block NEW connections, so it's not considered blocking
        result.EffectiveRule!.Name.Should().Be("Block Invalid Only");
        result.IsBlocked.Should().BeFalse(); // Because it doesn't block NEW connections
        result.IsAllowed.Should().BeFalse(); // It's a block action, just not effective
    }

    [Fact]
    public void Evaluate_MultipleAllowRules_ReturnsFirstByIndex()
    {
        var rules = new List<FirewallRule>
        {
            CreateRule("Allow 2", "ACCEPT", 200, "ANY", null),
            CreateRule("Allow 1", "ACCEPT", 100, "ANY", null),
            CreateRule("Allow 3", "ACCEPT", 300, "ANY", null)
        };

        var result = FirewallRuleEvaluator.Evaluate(rules, r => true);

        result.EffectiveRule!.Name.Should().Be("Allow 1");
    }

    #endregion

    #region IsTrafficBlocked Tests

    [Fact]
    public void IsTrafficBlocked_EffectiveBlockRule_ReturnsTrue()
    {
        var rules = new List<FirewallRule>
        {
            CreateRule("Block Rule", "DROP", 100, "ANY", null)
        };

        var result = FirewallRuleEvaluator.IsTrafficBlocked(rules, r => true);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsTrafficBlocked_AllowRuleEclipsesBlock_ReturnsFalse()
    {
        var rules = new List<FirewallRule>
        {
            CreateRule("Allow Rule", "ACCEPT", 100, "ANY", null),
            CreateRule("Block Rule", "DROP", 200, "ANY", null)
        };

        var result = FirewallRuleEvaluator.IsTrafficBlocked(rules, r => true);

        result.Should().BeFalse();
    }

    [Fact]
    public void IsTrafficBlocked_NoMatchingRules_ReturnsFalse()
    {
        var rules = new List<FirewallRule>();

        var result = FirewallRuleEvaluator.IsTrafficBlocked(rules, r => true);

        result.Should().BeFalse();
    }

    #endregion

    #region IsTrafficAllowed Tests

    [Fact]
    public void IsTrafficAllowed_EffectiveAllowRule_ReturnsTrue()
    {
        var rules = new List<FirewallRule>
        {
            CreateRule("Allow Rule", "ACCEPT", 100, "ANY", null)
        };

        var result = FirewallRuleEvaluator.IsTrafficAllowed(rules, r => true);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsTrafficAllowed_BlockRuleEclipsesAllow_ReturnsFalse()
    {
        var rules = new List<FirewallRule>
        {
            CreateRule("Block Rule", "DROP", 100, "ANY", null),
            CreateRule("Allow Rule", "ACCEPT", 200, "ANY", null)
        };

        var result = FirewallRuleEvaluator.IsTrafficAllowed(rules, r => true);

        result.Should().BeFalse();
    }

    [Fact]
    public void IsTrafficAllowed_NoMatchingRules_ReturnsFalse()
    {
        var rules = new List<FirewallRule>();

        var result = FirewallRuleEvaluator.IsTrafficAllowed(rules, r => true);

        result.Should().BeFalse();
    }

    #endregion

    #region GetEffectiveBlockRule Tests

    [Fact]
    public void GetEffectiveBlockRule_EffectiveBlockExists_ReturnsRule()
    {
        var rules = new List<FirewallRule>
        {
            CreateRule("Block Rule", "DROP", 100, "ANY", null)
        };

        var result = FirewallRuleEvaluator.GetEffectiveBlockRule(rules, r => true);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Block Rule");
    }

    [Fact]
    public void GetEffectiveBlockRule_AllowRuleEclipsesBlock_ReturnsNull()
    {
        var rules = new List<FirewallRule>
        {
            CreateRule("Allow Rule", "ACCEPT", 100, "ANY", null),
            CreateRule("Block Rule", "DROP", 200, "ANY", null)
        };

        var result = FirewallRuleEvaluator.GetEffectiveBlockRule(rules, r => true);

        result.Should().BeNull();
    }

    #endregion

    #region GetEffectiveAllowRule Tests

    [Fact]
    public void GetEffectiveAllowRule_EffectiveAllowExists_ReturnsRule()
    {
        var rules = new List<FirewallRule>
        {
            CreateRule("Allow Rule", "ACCEPT", 100, "ANY", null)
        };

        var result = FirewallRuleEvaluator.GetEffectiveAllowRule(rules, r => true);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Allow Rule");
    }

    [Fact]
    public void GetEffectiveAllowRule_BlockRuleEclipsesAllow_ReturnsNull()
    {
        var rules = new List<FirewallRule>
        {
            CreateRule("Block Rule", "DROP", 100, "ANY", null),
            CreateRule("Allow Rule", "ACCEPT", 200, "ANY", null)
        };

        var result = FirewallRuleEvaluator.GetEffectiveAllowRule(rules, r => true);

        result.Should().BeNull();
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Evaluate_RejectAction_TreatedAsBlock()
    {
        var rules = new List<FirewallRule>
        {
            CreateRule("Reject Rule", "REJECT", 100, "ANY", null)
        };

        var result = FirewallRuleEvaluator.Evaluate(rules, r => true);

        result.IsBlocked.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_DenyAction_TreatedAsBlock()
    {
        var rules = new List<FirewallRule>
        {
            CreateRule("Deny Rule", "DENY", 100, "ANY", null)
        };

        var result = FirewallRuleEvaluator.Evaluate(rules, r => true);

        result.IsBlocked.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_BlockWithConnectionStateAll_IsBlocking()
    {
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "1", Name = "Block All States", Action = "DROP", Index = 100,
                Enabled = true, SourceMatchingTarget = "ANY",
                ConnectionStateType = "ALL"
            }
        };

        var result = FirewallRuleEvaluator.Evaluate(rules, r => true);

        result.IsBlocked.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_BlockWithNewInCustomStates_IsBlocking()
    {
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "1", Name = "Block With NEW", Action = "DROP", Index = 100,
                Enabled = true, SourceMatchingTarget = "ANY",
                ConnectionStateType = "CUSTOM",
                ConnectionStates = new List<string> { "NEW", "ESTABLISHED" }
            }
        };

        var result = FirewallRuleEvaluator.Evaluate(rules, r => true);

        result.IsBlocked.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_PredicateFiltersCorrectly()
    {
        var rules = new List<FirewallRule>
        {
            CreateRule("Block Net-A", "DROP", 100, "NETWORK", new List<string> { "net-a" }),
            CreateRule("Allow Net-B", "ACCEPT", 100, "NETWORK", new List<string> { "net-b" })
        };

        // Only match net-b rules
        var result = FirewallRuleEvaluator.Evaluate(rules, r => r.SourceNetworkIds?.Contains("net-b") == true);

        result.EffectiveRule!.Name.Should().Be("Allow Net-B");
        result.IsAllowed.Should().BeTrue();
    }

    #endregion

    #region ForNewConnections Tests

    [Fact]
    public void Evaluate_ForNewConnections_SkipsRespondOnlyAllowRules()
    {
        // RESPOND_ONLY allow rule followed by block rule
        // Without forNewConnections, the allow rule would be effective
        // With forNewConnections, the allow rule is skipped and block becomes effective
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "1", Name = "Allow Return Traffic", Action = "ACCEPT", Index = 100,
                Enabled = true, SourceMatchingTarget = "ANY",
                ConnectionStateType = "RESPOND_ONLY",
                ConnectionStates = new List<string> { "ESTABLISHED", "RELATED" }
            },
            CreateRule("Block All Traffic", "DROP", 200, "ANY", null)
        };

        // Without forNewConnections - Allow Return Traffic is effective
        var resultDefault = FirewallRuleEvaluator.Evaluate(rules, r => true);
        resultDefault.EffectiveRule!.Name.Should().Be("Allow Return Traffic");
        resultDefault.IsAllowed.Should().BeTrue();

        // With forNewConnections - Allow Return Traffic is skipped, Block All Traffic is effective
        var resultForNew = FirewallRuleEvaluator.Evaluate(rules, r => true, forNewConnections: true);
        resultForNew.EffectiveRule!.Name.Should().Be("Block All Traffic");
        resultForNew.IsBlocked.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_ForNewConnections_AllowsRegularAllowRules()
    {
        // Regular allow rule (ALL connection states) should still be effective
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "1", Name = "Allow All Traffic", Action = "ACCEPT", Index = 100,
                Enabled = true, SourceMatchingTarget = "ANY",
                ConnectionStateType = "ALL"
            },
            CreateRule("Block All Traffic", "DROP", 200, "ANY", null)
        };

        var result = FirewallRuleEvaluator.Evaluate(rules, r => true, forNewConnections: true);

        result.EffectiveRule!.Name.Should().Be("Allow All Traffic");
        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_ForNewConnections_NoMatchingRulesAfterFiltering()
    {
        // Only RESPOND_ONLY allow rules - should return null when forNewConnections=true
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "1", Name = "Allow Return Traffic", Action = "ACCEPT", Index = 100,
                Enabled = true, SourceMatchingTarget = "ANY",
                ConnectionStateType = "RESPOND_ONLY"
            }
        };

        var result = FirewallRuleEvaluator.Evaluate(rules, r => true, forNewConnections: true);

        result.EffectiveRule.Should().BeNull();
        result.IsBlocked.Should().BeFalse();
        result.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_ForNewConnections_BlockRulesStillMatch()
    {
        // Block rules should always be considered regardless of forNewConnections
        var rules = new List<FirewallRule>
        {
            CreateRule("Block All Traffic", "DROP", 100, "ANY", null)
        };

        var result = FirewallRuleEvaluator.Evaluate(rules, r => true, forNewConnections: true);

        result.EffectiveRule!.Name.Should().Be("Block All Traffic");
        result.IsBlocked.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_ForNewConnections_MultipleRespondOnlySkipped()
    {
        // Multiple RESPOND_ONLY rules before a block rule
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "1", Name = "Allow Return 1", Action = "ACCEPT", Index = 100,
                Enabled = true, SourceMatchingTarget = "ANY",
                ConnectionStateType = "RESPOND_ONLY"
            },
            new FirewallRule
            {
                Id = "2", Name = "Allow Return 2", Action = "ACCEPT", Index = 150,
                Enabled = true, SourceMatchingTarget = "ANY",
                ConnectionStateType = "RESPOND_ONLY"
            },
            CreateRule("Block All Traffic", "DROP", 200, "ANY", null)
        };

        var result = FirewallRuleEvaluator.Evaluate(rules, r => true, forNewConnections: true);

        result.EffectiveRule!.Name.Should().Be("Block All Traffic");
        result.IsBlocked.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_ForNewConnections_EclipsedBlockRuleDetected()
    {
        // Regular allow rule followed by block rule - eclipsed block should be detected
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "1", Name = "Allow All", Action = "ACCEPT", Index = 100,
                Enabled = true, SourceMatchingTarget = "ANY",
                ConnectionStateType = "ALL"
            },
            CreateRule("Block All Traffic", "DROP", 200, "ANY", null)
        };

        var result = FirewallRuleEvaluator.Evaluate(rules, r => true, forNewConnections: true);

        result.EffectiveRule!.Name.Should().Be("Allow All");
        result.BlockRuleEclipsed.Should().BeTrue();
        result.EclipsedBlockRule!.Name.Should().Be("Block All Traffic");
    }

    #endregion

    #region Helper Methods

    private static FirewallRule CreateRule(
        string name,
        string action,
        int index,
        string sourceMatchingTarget,
        List<string>? sourceNetworkIds)
    {
        return new FirewallRule
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Action = action,
            Index = index,
            Enabled = true,
            SourceMatchingTarget = sourceMatchingTarget,
            SourceNetworkIds = sourceNetworkIds
        };
    }

    #endregion
}
