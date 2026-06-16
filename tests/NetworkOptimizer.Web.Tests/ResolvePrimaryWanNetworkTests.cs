using FluentAssertions;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// Tie-breaking rules for <see cref="UniFiConnectionService.ResolvePrimaryWanNetwork"/>:
/// among enabled WANs, weighted beats failover-only, highest weight wins, lowest failover
/// priority breaks ties, and the "WAN" networkgroup is the final tiebreak.
/// </summary>
public class ResolvePrimaryWanNetworkTests
{
    private static NetworkInfo Wan(
        string name, string networkgroup, bool enabled = true,
        string? lbType = null, int? weight = null, int? priority = null) =>
        new()
        {
            Name = name,
            Purpose = "wan",
            Enabled = enabled,
            WanNetworkgroup = networkgroup,
            WanLoadBalanceType = lbType,
            WanLoadBalanceWeight = weight,
            WanFailoverPriority = priority,
        };

    [Fact]
    public void Single_wan_is_primary()
    {
        var primary = UniFiConnectionService.ResolvePrimaryWanNetwork(new List<NetworkInfo> { Wan("A", "WAN") });
        primary.Should().NotBeNull();
        primary!.Name.Should().Be("A");
    }

    [Fact]
    public void No_wan_returns_null()
    {
        UniFiConnectionService.ResolvePrimaryWanNetwork(new List<NetworkInfo>()).Should().BeNull();
        UniFiConnectionService.ResolvePrimaryWanNetwork(new List<NetworkInfo>
        {
            new() { Name = "LAN", Purpose = "corporate", Enabled = true },
        }).Should().BeNull();
    }

    [Fact]
    public void Weighted_beats_failover_only()
    {
        var nets = new List<NetworkInfo>
        {
            Wan("fail", "WAN", lbType: "failover-only", priority: 1),
            Wan("weighted", "WAN2", lbType: "weighted", weight: 10),
        };
        UniFiConnectionService.ResolvePrimaryWanNetwork(nets)!.Name.Should().Be("weighted");
    }

    [Fact]
    public void Highest_weight_wins()
    {
        var nets = new List<NetworkInfo>
        {
            Wan("low", "WAN2", lbType: "weighted", weight: 50),
            Wan("high", "WAN", lbType: "weighted", weight: 99),
        };
        UniFiConnectionService.ResolvePrimaryWanNetwork(nets)!.Name.Should().Be("high");
    }

    [Fact]
    public void Lowest_failover_priority_breaks_tie()
    {
        var nets = new List<NetworkInfo>
        {
            Wan("p2", "WAN2", lbType: "failover-only", priority: 2),
            Wan("p1", "WAN", lbType: "failover-only", priority: 1),
        };
        UniFiConnectionService.ResolvePrimaryWanNetwork(nets)!.Name.Should().Be("p1");
    }

    [Fact]
    public void Disabled_wan_is_excluded()
    {
        var nets = new List<NetworkInfo>
        {
            Wan("disabled", "WAN", enabled: false, lbType: "weighted", weight: 99),
            Wan("enabled", "WAN2", enabled: true, lbType: "weighted", weight: 10),
        };
        UniFiConnectionService.ResolvePrimaryWanNetwork(nets)!.Name.Should().Be("enabled");
    }

    [Fact]
    public void Networkgroup_WAN_is_the_final_tiebreak()
    {
        // Identical load-balance config across both - only the networkgroup distinguishes them.
        var nets = new List<NetworkInfo>
        {
            Wan("second", "WAN2", lbType: "weighted", weight: 50, priority: 1),
            Wan("first", "WAN", lbType: "weighted", weight: 50, priority: 1),
        };
        UniFiConnectionService.ResolvePrimaryWanNetwork(nets)!.WanNetworkgroup.Should().Be("WAN");
    }
}
