using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Models;

public class FirewallRuleTests
{
    #region BlocksNewConnections Tests

    [Fact]
    public void BlocksNewConnections_NoConnectionStateType_ReturnsTrue()
    {
        // No connection state type = blocks all traffic (legacy behavior)
        var rule = new FirewallRule
        {
            Id = "test",
            ConnectionStateType = null
        };

        rule.BlocksNewConnections().Should().BeTrue();
    }

    [Fact]
    public void BlocksNewConnections_EmptyConnectionStateType_ReturnsTrue()
    {
        // Empty string = blocks all traffic
        var rule = new FirewallRule
        {
            Id = "test",
            ConnectionStateType = ""
        };

        rule.BlocksNewConnections().Should().BeTrue();
    }

    [Fact]
    public void BlocksNewConnections_ConnectionStateTypeAll_ReturnsTrue()
    {
        // ALL = blocks all connection states including NEW
        var rule = new FirewallRule
        {
            Id = "test",
            ConnectionStateType = "ALL"
        };

        rule.BlocksNewConnections().Should().BeTrue();
    }

    [Fact]
    public void BlocksNewConnections_ConnectionStateTypeAllLowercase_ReturnsTrue()
    {
        // Case insensitive - "all" should work
        var rule = new FirewallRule
        {
            Id = "test",
            ConnectionStateType = "all"
        };

        rule.BlocksNewConnections().Should().BeTrue();
    }

    [Fact]
    public void BlocksNewConnections_CustomWithOnlyInvalid_ReturnsFalse()
    {
        // CUSTOM with only INVALID = doesn't block NEW connections
        var rule = new FirewallRule
        {
            Id = "test",
            ConnectionStateType = "CUSTOM",
            ConnectionStates = new List<string> { "INVALID" }
        };

        rule.BlocksNewConnections().Should().BeFalse();
    }

    [Fact]
    public void BlocksNewConnections_CustomWithNewState_ReturnsTrue()
    {
        // CUSTOM with NEW = blocks NEW connections
        var rule = new FirewallRule
        {
            Id = "test",
            ConnectionStateType = "CUSTOM",
            ConnectionStates = new List<string> { "NEW" }
        };

        rule.BlocksNewConnections().Should().BeTrue();
    }

    [Fact]
    public void BlocksNewConnections_CustomWithAllStates_ReturnsTrue()
    {
        // CUSTOM with all states including NEW
        var rule = new FirewallRule
        {
            Id = "test",
            ConnectionStateType = "CUSTOM",
            ConnectionStates = new List<string> { "NEW", "ESTABLISHED", "RELATED", "INVALID" }
        };

        rule.BlocksNewConnections().Should().BeTrue();
    }

    [Fact]
    public void BlocksNewConnections_CustomWithEstablishedRelatedInvalid_ReturnsFalse()
    {
        // CUSTOM without NEW (only ESTABLISHED, RELATED, INVALID) = doesn't block NEW connections
        var rule = new FirewallRule
        {
            Id = "test",
            ConnectionStateType = "CUSTOM",
            ConnectionStates = new List<string> { "ESTABLISHED", "RELATED", "INVALID" }
        };

        rule.BlocksNewConnections().Should().BeFalse();
    }

    [Fact]
    public void BlocksNewConnections_CustomWithNewLowercase_ReturnsTrue()
    {
        // Case insensitive - "new" should work
        var rule = new FirewallRule
        {
            Id = "test",
            ConnectionStateType = "custom",
            ConnectionStates = new List<string> { "new" }
        };

        rule.BlocksNewConnections().Should().BeTrue();
    }

    [Fact]
    public void BlocksNewConnections_CustomWithNullConnectionStates_ReturnsFalse()
    {
        // CUSTOM with null connection states = no states specified, doesn't block NEW
        var rule = new FirewallRule
        {
            Id = "test",
            ConnectionStateType = "CUSTOM",
            ConnectionStates = null
        };

        rule.BlocksNewConnections().Should().BeFalse();
    }

    [Fact]
    public void BlocksNewConnections_CustomWithEmptyConnectionStates_ReturnsFalse()
    {
        // CUSTOM with empty list = no states specified, doesn't block NEW
        var rule = new FirewallRule
        {
            Id = "test",
            ConnectionStateType = "CUSTOM",
            ConnectionStates = new List<string>()
        };

        rule.BlocksNewConnections().Should().BeFalse();
    }

    [Fact]
    public void BlocksNewConnections_UnknownConnectionStateType_ReturnsTrue()
    {
        // Unknown type - be conservative and assume it might block NEW
        var rule = new FirewallRule
        {
            Id = "test",
            ConnectionStateType = "UNKNOWN"
        };

        rule.BlocksNewConnections().Should().BeTrue();
    }

    #endregion
}
