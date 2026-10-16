using System.Text.Json;
using NetworkOptimizer.Threats.Analysis;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

public class FlowInterestFilterTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void IsInvalidStateDrop_OnlyPredefinedInvalidTraffic_ReturnsTrue()
    {
        var flow = Parse("""{"action": "blocked", "policies": [{"internal_type": "PREDEFINED_FIREWALL_RULE", "name": "Block Invalid Traffic", "type": "FIREWALL"}]}""");
        Assert.True(FlowInterestFilter.IsInvalidStateDrop(flow));
    }

    [Fact]
    public void IsInvalidStateDrop_AlsoBlockedByAnotherPolicy_ReturnsFalse()
    {
        var flow = Parse("""{"action": "blocked", "policies": [{"internal_type": "PREDEFINED_FIREWALL_RULE", "name": "Block Invalid Traffic"}, {"internal_type": "PREDEFINED_FIREWALL_RULE", "name": "Block All Traffic"}]}""");
        Assert.False(FlowInterestFilter.IsInvalidStateDrop(flow));
    }

    [Fact]
    public void IsInvalidStateDrop_CustomPolicyWithTheSameName_ReturnsFalse()
    {
        var flow = Parse("""{"action": "blocked", "policies": [{"internal_type": "FIREWALL_POLICY", "name": "Block Invalid Traffic"}]}""");
        Assert.False(FlowInterestFilter.IsInvalidStateDrop(flow));
    }

    [Fact]
    public void IsInvalidStateDrop_NoPolicies_ReturnsFalse()
    {
        Assert.False(FlowInterestFilter.IsInvalidStateDrop(Parse("""{"action": "blocked"}""")));
        Assert.False(FlowInterestFilter.IsInvalidStateDrop(Parse("""{"action": "blocked", "policies": []}""")));
    }

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

    [Fact]
    public void IsInteresting_OutgoingSuspiciousPort4444_ReturnsTrue()
    {
        var flow = Parse("""{"action": "allowed", "risk": "low", "direction": "outgoing", "destination": {"port": 4444}}""");
        Assert.True(FlowInterestFilter.IsInteresting(flow));
    }

    [Fact]
    public void IsInteresting_OutgoingSuspiciousPortIRC6667_ReturnsTrue()
    {
        var flow = Parse("""{"action": "allowed", "risk": "low", "direction": "outgoing", "destination": {"port": 6667}}""");
        Assert.True(FlowInterestFilter.IsInteresting(flow));
    }

    [Fact]
    public void IsInteresting_OutgoingNormalPort443_LowRisk_ReturnsFalse()
    {
        var flow = Parse("""{"action": "allowed", "risk": "low", "direction": "outgoing", "destination": {"port": 443}}""");
        Assert.False(FlowInterestFilter.IsInteresting(flow));
    }

    private static Models.ThreatEvent Blocked(string ip, int sourcePort, int destPort, string direction = "incoming") => new()
    {
        SourceIp = ip,
        SourcePort = sourcePort,
        DestPort = destPort,
        Direction = direction,
        Action = Models.ThreatAction.Blocked,
        EventSource = Models.EventSource.TrafficFlow,
    };

    [Fact]
    public void IsReplyCandidate_ServerPortToEphemeral_FromPublic()
    {
        Assert.True(FlowInterestFilter.IsReplyCandidate(Blocked("203.0.113.7", 443, 59469)));
        Assert.False(FlowInterestFilter.IsReplyCandidate(Blocked("203.0.113.7", 443, 22)));
        Assert.False(FlowInterestFilter.IsReplyCandidate(Blocked("203.0.113.7", 40000, 59469)));
        Assert.False(FlowInterestFilter.IsReplyCandidate(Blocked("192.0.2.1", 443, 59469, direction: "outgoing")));
        Assert.False(FlowInterestFilter.IsReplyCandidate(Blocked("10.0.0.5", 443, 59469)));
    }

    [Fact]
    public void OutgoingConnections_ReadsAllowedFlows_AndMatchesTheClientPort()
    {
        var response = Parse("""
            {"data": [
              {"action": "allowed", "direction": "outgoing", "source": {"ip": "192.168.1.20", "port": 59469}, "destination": {"ip": "203.0.113.7", "port": 443}},
              {"action": "blocked", "direction": "outgoing", "source": {"ip": "192.168.1.21", "port": 50000}, "destination": {"ip": "203.0.113.7", "port": 443}},
              {"action": "allowed", "direction": "outgoing", "source": {"ip": "192.168.1.22"}, "destination": {"ip": "203.0.113.7", "port": 443}}
            ]}
            """);
        var outgoing = FlowInterestFilter.OutgoingConnections(response).ToHashSet();

        Assert.Single(outgoing);
        Assert.True(FlowInterestFilter.IsReplyTo(Blocked("203.0.113.7", 443, 59469), outgoing));
        // Same server, another client port: a scanner that sent from 443 does not match.
        Assert.False(FlowInterestFilter.IsReplyTo(Blocked("203.0.113.7", 443, 59470), outgoing));
        Assert.False(FlowInterestFilter.IsReplyTo(Blocked("203.0.113.7", 80, 59469), outgoing));
        Assert.False(FlowInterestFilter.IsReplyTo(Blocked("203.0.113.50", 443, 59469), outgoing));
    }
}
