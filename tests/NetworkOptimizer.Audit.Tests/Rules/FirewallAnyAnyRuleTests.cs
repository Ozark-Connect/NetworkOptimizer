using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Rules;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Rules;

public class FirewallAnyAnyRuleTests
{
    #region IsAnyAnyRule Tests

    [Fact]
    public void IsAnyAnyRule_DisabledRule_ReturnsFalse()
    {
        // Arrange
        var rule = CreateFirewallRule(
            enabled: false,
            sourceType: "any",
            destType: "any",
            protocol: "all",
            action: "accept");

        // Act
        var result = FirewallAnyAnyRule.IsAnyAnyRule(rule);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsAnyAnyRule_AnySourceAnyDestAllProtocolAccept_ReturnsTrue()
    {
        // Arrange
        var rule = CreateFirewallRule(
            enabled: true,
            sourceType: "any",
            destType: "any",
            protocol: "all",
            action: "accept");

        // Act
        var result = FirewallAnyAnyRule.IsAnyAnyRule(rule);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsAnyAnyRule_EmptySourceAndDest_ReturnsTrue()
    {
        // Arrange
        var rule = CreateFirewallRule(
            enabled: true,
            sourceType: null,
            source: null,
            destType: null,
            destination: null,
            protocol: null,
            action: "accept");

        // Act
        var result = FirewallAnyAnyRule.IsAnyAnyRule(rule);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsAnyAnyRule_DropAction_ReturnsFalse()
    {
        // Arrange - even with any->any, drop action is fine
        var rule = CreateFirewallRule(
            enabled: true,
            sourceType: "any",
            destType: "any",
            protocol: "all",
            action: "drop");

        // Act
        var result = FirewallAnyAnyRule.IsAnyAnyRule(rule);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsAnyAnyRule_RejectAction_ReturnsFalse()
    {
        // Arrange
        var rule = CreateFirewallRule(
            enabled: true,
            sourceType: "any",
            destType: "any",
            protocol: "all",
            action: "reject");

        // Act
        var result = FirewallAnyAnyRule.IsAnyAnyRule(rule);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsAnyAnyRule_SpecificSource_ReturnsFalse()
    {
        // Arrange
        var rule = CreateFirewallRule(
            enabled: true,
            sourceType: "address",
            source: "192.168.1.0/24",
            destType: "any",
            protocol: "all",
            action: "accept");

        // Act
        var result = FirewallAnyAnyRule.IsAnyAnyRule(rule);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsAnyAnyRule_SpecificDestination_ReturnsFalse()
    {
        // Arrange
        var rule = CreateFirewallRule(
            enabled: true,
            sourceType: "any",
            destType: "address",
            destination: "10.0.0.0/8",
            protocol: "all",
            action: "accept");

        // Act
        var result = FirewallAnyAnyRule.IsAnyAnyRule(rule);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsAnyAnyRule_SpecificProtocol_ReturnsFalse()
    {
        // Arrange
        var rule = CreateFirewallRule(
            enabled: true,
            sourceType: "any",
            destType: "any",
            protocol: "tcp",
            action: "accept");

        // Act
        var result = FirewallAnyAnyRule.IsAnyAnyRule(rule);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region CreateIssue Tests

    [Fact]
    public void CreateIssue_ReturnsCorrectSeverity()
    {
        // Arrange
        var rule = CreateFirewallRule(
            name: "Allow All",
            id: "rule-123");

        // Act
        var issue = FirewallAnyAnyRule.CreateIssue(rule);

        // Assert
        issue.Severity.Should().Be(AuditSeverity.Critical);
    }

    [Fact]
    public void CreateIssue_ReturnsCorrectType()
    {
        // Arrange
        var rule = CreateFirewallRule(name: "Allow All");

        // Act
        var issue = FirewallAnyAnyRule.CreateIssue(rule);

        // Assert
        issue.Type.Should().Be("FW_ANY_ANY");
    }

    [Fact]
    public void CreateIssue_ScoreImpactIs15()
    {
        // Arrange
        var rule = CreateFirewallRule(name: "Allow All");

        // Act
        var issue = FirewallAnyAnyRule.CreateIssue(rule);

        // Assert
        issue.ScoreImpact.Should().Be(15);
    }

    [Fact]
    public void CreateIssue_IncludesRuleName()
    {
        // Arrange
        var rule = CreateFirewallRule(name: "Allow All Traffic");

        // Act
        var issue = FirewallAnyAnyRule.CreateIssue(rule);

        // Assert
        issue.Message.Should().Contain("Allow All Traffic");
    }

    [Fact]
    public void CreateIssue_IncludesMetadata()
    {
        // Arrange
        var rule = CreateFirewallRule(
            id: "rule-456",
            name: "Test Rule",
            index: 5,
            ruleset: "LAN_IN",
            action: "accept");

        // Act
        var issue = FirewallAnyAnyRule.CreateIssue(rule);

        // Assert
        issue.Metadata.Should().ContainKey("rule_id");
        issue.Metadata!["rule_id"].Should().Be("rule-456");
        issue.Metadata.Should().ContainKey("rule_name");
        issue.Metadata["rule_name"].Should().Be("Test Rule");
        issue.Metadata.Should().ContainKey("rule_index");
        issue.Metadata["rule_index"].Should().Be(5);
        issue.Metadata.Should().ContainKey("ruleset");
        issue.Metadata["ruleset"].Should().Be("LAN_IN");
    }

    [Fact]
    public void CreateIssue_IncludesRecommendedAction()
    {
        // Arrange
        var rule = CreateFirewallRule(name: "Allow All");

        // Act
        var issue = FirewallAnyAnyRule.CreateIssue(rule);

        // Assert
        issue.RecommendedAction.Should().NotBeNullOrEmpty();
        issue.RecommendedAction.Should().Contain("Restrict");
    }

    [Fact]
    public void IsAnyAnyRule_WithDestinationPort_ReturnsFalse()
    {
        // A rule with any->any but specific destination ports (e.g., from a port group)
        // is NOT truly any->any - the ports restrict what can be accessed
        var rule = CreateFirewallRule(
            enabled: true,
            sourceType: "any",
            destType: "any",
            protocol: "all",
            action: "accept",
            destinationPort: "80,443");

        var result = FirewallAnyAnyRule.IsAnyAnyRule(rule);

        result.Should().BeFalse();
    }

    [Fact]
    public void IsAnyAnyRule_WithSourcePort_ReturnsFalse()
    {
        var rule = CreateFirewallRule(
            enabled: true,
            sourceType: "any",
            destType: "any",
            protocol: "all",
            action: "accept",
            sourcePort: "1024-65535");

        var result = FirewallAnyAnyRule.IsAnyAnyRule(rule);

        result.Should().BeFalse();
    }

    #endregion

    #region Helper Methods

    private static FirewallRule CreateFirewallRule(
        bool enabled = true,
        string? sourceType = "any",
        string? source = null,
        string? destType = "any",
        string? destination = null,
        string? protocol = "all",
        string? action = "accept",
        string? name = "Test Rule",
        string id = "rule-1",
        int index = 1,
        string? ruleset = "LAN_IN",
        string? destinationPort = null,
        string? sourcePort = null)
    {
        return new FirewallRule
        {
            Id = id,
            Name = name,
            Enabled = enabled,
            Index = index,
            Action = action,
            Protocol = protocol,
            SourceType = sourceType,
            Source = source,
            DestinationType = destType,
            Destination = destination,
            Ruleset = ruleset,
            DestinationPort = destinationPort,
            SourcePort = sourcePort
        };
    }

    #endregion
}
