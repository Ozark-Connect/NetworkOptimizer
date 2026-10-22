using FluentAssertions;
using NetworkOptimizer.Threats.WanFlood;
using NetworkOptimizer.Web.Services.WanFlood;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

/// <summary>
/// "Needs UniFi OS 6.0" is shown only for an unsupported gateway on 5.x or older; any other reason, or an unknown
/// version, keeps the generic message.
/// </summary>
public class WanFloodOverviewTests
{
    private static WanFloodOverview Overview(WanFloodAgentState state, string? firmware) =>
        new(new WanFloodSettings(), null, false, null) { AgentState = state, GatewayFirmware = firmware };

    [Theory]
    [InlineData("5.1.33", true)]
    [InlineData("4.4.9", true)]
    [InlineData("UDMEA4C.cn10k.v5.1.33.44ce47b.260909.0025", true)]
    [InlineData("6.0.0", false)]
    [InlineData("6.0.13", false)]
    [InlineData("7.0.1", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsBeforeUniFiOs6(string? firmware, bool expected) =>
        WanFloodOverview.IsBeforeUniFiOs6(firmware).Should().Be(expected);

    [Fact]
    public void Unsupported_On5x_NeedsUniFiOs6() =>
        Overview(WanFloodAgentState.Unsupported, "5.1.33").NeedsUniFiOs6.Should().BeTrue();

    [Fact]
    public void Unsupported_On6x_DoesNotBlameTheVersion() =>
        Overview(WanFloodAgentState.Unsupported, "6.0.13").NeedsUniFiOs6.Should().BeFalse();

    [Fact]
    public void Unsupported_UnknownVersion_DoesNotBlameTheVersion() =>
        Overview(WanFloodAgentState.Unsupported, null).NeedsUniFiOs6.Should().BeFalse();

    [Theory]
    [InlineData(WanFloodAgentState.NeedsUpdate)]
    [InlineData(WanFloodAgentState.NoAgent)]
    [InlineData(WanFloodAgentState.Capable)]
    public void OtherStates_On5x_DoNotNeedUniFiOs6(WanFloodAgentState state) =>
        Overview(state, "5.1.33").NeedsUniFiOs6.Should().BeFalse();
}
