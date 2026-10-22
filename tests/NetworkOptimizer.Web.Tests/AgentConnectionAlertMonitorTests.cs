using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class AgentConnectionAlertMonitorTests
{
    [Theory]
    [InlineData("Beacon", "Agent \"Beacon\"")]
    [InlineData("North Office", "Agent \"North Office\"")]
    public void AgentLabel_DistinctiveName_KeepsQuotedName(string name, string expected)
        => AgentConnectionAlertMonitor.AgentLabel(name).Should().Be(expected);

    [Theory]
    [InlineData("agent")]
    [InlineData("Agent")]
    [InlineData("AGENT")]
    [InlineData("  agent  ")] // trimmed to the generic default
    [InlineData("Agent 2")]
    [InlineData("agent 12")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AgentLabel_GenericOrEmptyName_UsesPlainReference(string? name)
        => AgentConnectionAlertMonitor.AgentLabel(name).Should().Be("The On-Site Agent");

    [Fact]
    public void AgentLabel_AgentPrefixWithoutDigits_IsDistinctive()
        => AgentConnectionAlertMonitor.AgentLabel("Agentsmith").Should().Be("Agent \"Agentsmith\"");

    [Theory]
    [InlineData("Agent 2", true, "203.0.113.5", "The Gateway Agent")]
    [InlineData("Edge", true, null, "The Gateway Agent \"Edge\"")]
    [InlineData("Agent", false, "192.0.2.10", "The On-Site Agent on 192.0.2.10")]
    [InlineData("Closet Box", false, "192.0.2.10", "The On-Site Agent \"Closet Box\" on 192.0.2.10")]
    [InlineData("Agent", false, null, "The On-Site Agent")]
    public void AgentSubject_NamesTheKind(string name, bool onGateway, string? lanIp, string expected)
        => AgentConnectionAlertMonitor.AgentSubject(name, onGateway, lanIp).Should().Be(expected);
}
