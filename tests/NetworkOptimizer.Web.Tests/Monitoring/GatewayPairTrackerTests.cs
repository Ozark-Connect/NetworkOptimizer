using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Which MACs make up a site's HA gateway. A Console Shadow Mode pair shows one row at a time, so
/// the set is built across takeovers; a Managed Shadow Gateway pair shows both rows at once.
/// </summary>
public class GatewayPairTrackerTests
{
    private const string A = "aa:bb:cc:00:00:01";
    private const string B = "aa:bb:cc:00:00:02";

    private static DiscoveredDevice Gateway(string mac, string? vrrp = null, GatewayHaRole role = GatewayHaRole.None,
        string? mode = null) => new()
    {
        Mac = mac,
        Type = DeviceType.Gateway,
        HardwareType = DeviceType.Gateway,
        VrrpState = vrrp,
        HaRole = role,
        DeviceModeOverride = mode,
        State = 1,
    };

    [Fact]
    public void ConsolePair_TakeoverAddsTheNewUnit_NewestFirst()
    {
        var before = GatewayPairTracker.Next([], [Gateway(A, "master")]);
        before.Should().Equal(A);

        var after = GatewayPairTracker.Next(before, [Gateway(B, "master")]);
        after.Should().Equal(B, A);
    }

    [Fact]
    public void ManagedPair_BothRowsAtOnce()
    {
        var devices = new[]
        {
            Gateway(A, role: GatewayHaRole.Primary),
            Gateway(B, "backup", GatewayHaRole.Shadow, GatewayHa.ShadowModeOverride),
        };

        GatewayPairTracker.Next([], devices).Should().BeEquivalentTo([A, B]);
    }

    [Fact]
    public void LoneGateway_WithoutVrrp_AddsNothing_AndKeepsHistory()
    {
        GatewayPairTracker.Next([], [Gateway(A)]).Should().BeEmpty();
        GatewayPairTracker.Next([B, A], [Gateway(A)]).Should().Equal(B, A);
    }

    [Fact]
    public void KeepsAtMostFour()
    {
        var current = new[] { "aa:bb:cc:00:00:11", "aa:bb:cc:00:00:12", "aa:bb:cc:00:00:13", "aa:bb:cc:00:00:14" };

        var next = GatewayPairTracker.Next(current, [Gateway(A, "master")]);

        next.Should().HaveCount(GatewayPairTracker.MaxMacs);
        next[0].Should().Be(A);
    }

    [Fact]
    public void Parse_NormalizesAndDedupes()
    {
        GatewayPairTracker.Parse("AA-BB-CC-00-00-01, aa:bb:cc:00:00:01,aa:bb:cc:00:00:02").Should().Equal(A, B);
        GatewayPairTracker.Parse(null).Should().BeEmpty();
    }
}
