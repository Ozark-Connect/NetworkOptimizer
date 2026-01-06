using NetworkOptimizer.Audit.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Models;

public class FirewallActionTests
{
    [Theory]
    [InlineData("allow", FirewallAction.Allow)]
    [InlineData("ALLOW", FirewallAction.Allow)]
    [InlineData("Allow", FirewallAction.Allow)]
    [InlineData("accept", FirewallAction.Accept)]
    [InlineData("ACCEPT", FirewallAction.Accept)]
    [InlineData("drop", FirewallAction.Drop)]
    [InlineData("DROP", FirewallAction.Drop)]
    [InlineData("deny", FirewallAction.Deny)]
    [InlineData("reject", FirewallAction.Reject)]
    [InlineData("block", FirewallAction.Block)]
    public void Parse_ValidActions_ReturnsCorrectEnum(string input, FirewallAction expected)
    {
        var result = FirewallActionExtensions.Parse(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("invalid")]
    [InlineData("permit")]
    public void Parse_InvalidActions_ReturnsUnknown(string? input)
    {
        var result = FirewallActionExtensions.Parse(input);
        Assert.Equal(FirewallAction.Unknown, result);
    }

    [Theory]
    [InlineData(FirewallAction.Allow, true)]
    [InlineData(FirewallAction.Accept, true)]
    [InlineData(FirewallAction.Drop, false)]
    [InlineData(FirewallAction.Deny, false)]
    [InlineData(FirewallAction.Reject, false)]
    [InlineData(FirewallAction.Block, false)]
    [InlineData(FirewallAction.Unknown, false)]
    public void IsAllowAction_ReturnsCorrectResult(FirewallAction action, bool expected)
    {
        Assert.Equal(expected, action.IsAllowAction());
    }

    [Theory]
    [InlineData(FirewallAction.Drop, true)]
    [InlineData(FirewallAction.Deny, true)]
    [InlineData(FirewallAction.Reject, true)]
    [InlineData(FirewallAction.Block, true)]
    [InlineData(FirewallAction.Allow, false)]
    [InlineData(FirewallAction.Accept, false)]
    [InlineData(FirewallAction.Unknown, false)]
    public void IsBlockAction_ReturnsCorrectResult(FirewallAction action, bool expected)
    {
        Assert.Equal(expected, action.IsBlockAction());
    }

    [Fact]
    public void FirewallRule_ActionType_ParsesCorrectly()
    {
        var rule = new FirewallRule { Id = "test", Action = "accept" };
        Assert.Equal(FirewallAction.Accept, rule.ActionType);
        Assert.True(rule.ActionType.IsAllowAction());
        Assert.False(rule.ActionType.IsBlockAction());
    }

    [Fact]
    public void FirewallRule_ActionType_NullAction_ReturnsUnknown()
    {
        var rule = new FirewallRule { Id = "test", Action = null };
        Assert.Equal(FirewallAction.Unknown, rule.ActionType);
        Assert.False(rule.ActionType.IsAllowAction());
        Assert.False(rule.ActionType.IsBlockAction());
    }
}
