using NetworkOptimizer.Web.Components.Pages;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

public class MonitoringToolsStatusTests
{
    [Theory]
    [InlineData("UNKNOWN", "status-neutral")]
    [InlineData("unknown", "status-neutral")]
    [InlineData(null, "status-neutral")]
    [InlineData("DOWN", "status-error")]
    [InlineData("UP", "status-connected")]
    public void GatewayInterfaceStatusClass_MatchesTheReportedState(string? state, string expectedClass)
    {
        Assert.Equal(expectedClass, MonitoringTools.GatewayInterfaceStatusClass(state));
    }
}
