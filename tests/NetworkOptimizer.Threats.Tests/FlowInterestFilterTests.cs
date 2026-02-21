using System.Text.Json;
using NetworkOptimizer.Threats.Analysis;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

public class FlowInterestFilterTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void IsInteresting_BlockedAction_ReturnsTrue()
    {
        var flow = Parse("""{"action": "blocked", "risk": "low", "direction": "incoming", "destination": {"port": 80}}""");
        Assert.True(FlowInterestFilter.IsInteresting(flow));
    }

    [Fact]
    public void IsInteresting_HighRisk_ReturnsTrue()
    {
        var flow = Parse("""{"action": "allowed", "risk": "high", "direction": "outgoing", "destination": {"port": 443}}""");
        Assert.True(FlowInterestFilter.IsInteresting(flow));
    }

    [Fact]
    public void IsInteresting_MediumRisk_ReturnsTrue()
    {
        var flow = Parse("""{"action": "allowed", "risk": "medium", "direction": "incoming", "destination": {"port": 80}}""");
        Assert.True(FlowInterestFilter.IsInteresting(flow));
    }

    [Fact]
    public void IsInteresting_IncomingSensitivePort_ReturnsTrue()
    {
        var flow = Parse("""{"action": "allowed", "risk": "low", "direction": "incoming", "destination": {"port": 22}}""");
        Assert.True(FlowInterestFilter.IsInteresting(flow));
    }

    [Fact]
    public void IsInteresting_IncomingRdp_ReturnsTrue()
    {
        var flow = Parse("""{"action": "allowed", "risk": "low", "direction": "incoming", "destination": {"port": 3389}}""");
        Assert.True(FlowInterestFilter.IsInteresting(flow));
    }

    [Fact]
    public void IsInteresting_LowRiskAllowedOutgoingNormalPort_ReturnsFalse()
    {
        var flow = Parse("""{"action": "allowed", "risk": "low", "direction": "outgoing", "destination": {"port": 443}}""");
        Assert.False(FlowInterestFilter.IsInteresting(flow));
    }

    [Fact]
    public void IsInteresting_LowRiskAllowedIncomingNormalPort_ReturnsFalse()
    {
        var flow = Parse("""{"action": "allowed", "risk": "low", "direction": "incoming", "destination": {"port": 443}}""");
        Assert.False(FlowInterestFilter.IsInteresting(flow));
    }

    [Fact]
    public void IsInteresting_CaseInsensitiveAction_ReturnsTrue()
    {
        var flow = Parse("""{"action": "BLOCKED", "risk": "low", "direction": "incoming", "destination": {"port": 80}}""");
        Assert.True(FlowInterestFilter.IsInteresting(flow));
    }

    [Fact]
    public void IsInteresting_CaseInsensitiveRisk_ReturnsTrue()
    {
        var flow = Parse("""{"action": "allowed", "risk": "HIGH", "direction": "outgoing", "destination": {"port": 443}}""");
        Assert.True(FlowInterestFilter.IsInteresting(flow));
    }

    [Fact]
    public void IsInteresting_MissingFields_ReturnsFalse()
    {
        var flow = Parse("""{}""");
        Assert.False(FlowInterestFilter.IsInteresting(flow));
    }

    [Fact]
    public void IsInteresting_IncomingSqlServer_ReturnsTrue()
    {
        var flow = Parse("""{"action": "allowed", "risk": "low", "direction": "incoming", "destination": {"port": 1433}}""");
        Assert.True(FlowInterestFilter.IsInteresting(flow));
    }
}
