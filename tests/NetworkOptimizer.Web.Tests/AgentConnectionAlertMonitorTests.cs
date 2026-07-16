using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class AgentConnectionAlertMonitorTests
{
    [Theory]
    [InlineData("Falkor", "Agent \"Falkor\"")]
    [InlineData("Honeybee Home", "Agent \"Honeybee Home\"")]
    public void AgentLabel_DistinctiveName_KeepsQuotedName(string name, string expected)
        => AgentConnectionAlertMonitor.AgentLabel(name).Should().Be(expected);

    [Theory]
    [InlineData("agent")]
    [InlineData("Agent")]
    [InlineData("AGENT")]
    [InlineData("  agent  ")] // trimmed to the generic default
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AgentLabel_GenericOrEmptyName_UsesPlainReference(string? name)
        => AgentConnectionAlertMonitor.AgentLabel(name).Should().Be("The On-Site Agent");
}
