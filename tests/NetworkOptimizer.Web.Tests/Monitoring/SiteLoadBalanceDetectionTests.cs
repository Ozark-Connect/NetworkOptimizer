using FluentAssertions;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Whether the site spreads traffic across WANs decides what an unpinned probe is worth: under
/// failover-only every unpinned box leaves by the primary and measures it honestly, while under
/// load balancing the same probe is spread across WANs and attributable to none of them.
/// </summary>
public class SiteLoadBalanceDetectionTests
{
    private static NetworkInfo Wan(string group, string? lbType, bool enabled = true) => new()
    {
        Name = group,
        Purpose = "wan",
        Enabled = enabled,
        WanNetworkgroup = group,
        WanLoadBalanceType = lbType,
    };

    [Fact]
    public void OneWan_IsNotLoadBalancing()
    {
        UniFiConnectionService.ResolveSiteLoadBalances(new[] { Wan("WAN", null) }).Should().BeFalse();
    }

    [Fact]
    public void APrimaryWithAFailoverOnlyBackup_IsNotLoadBalancing()
    {
        UniFiConnectionService.ResolveSiteLoadBalances(new[]
        {
            Wan("WAN", null),
            Wan("WAN2", "failover-only"),
        }).Should().BeFalse();
    }

    [Fact]
    public void TwoWeightedWans_AreLoadBalancing()
    {
        UniFiConnectionService.ResolveSiteLoadBalances(new[]
        {
            Wan("WAN", null),
            Wan("WAN2", "weighted"),
        }).Should().BeTrue();
    }

    [Fact]
    public void ADisabledSecondWan_DoesNotCount()
    {
        UniFiConnectionService.ResolveSiteLoadBalances(new[]
        {
            Wan("WAN", null),
            Wan("WAN2", "weighted", enabled: false),
        }).Should().BeFalse();
    }

    [Fact]
    public void ThreeWansWithOneOnFailover_StillLoadBalanceTheOtherTwo()
    {
        UniFiConnectionService.ResolveSiteLoadBalances(new[]
        {
            Wan("WAN", null),
            Wan("WAN2", "weighted"),
            Wan("WAN3", "failover-only"),
        }).Should().BeTrue();
    }
}
