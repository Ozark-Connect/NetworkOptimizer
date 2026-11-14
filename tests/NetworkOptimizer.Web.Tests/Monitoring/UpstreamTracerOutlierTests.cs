using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

public class UpstreamTracerOutlierTests
{
    private static TransitAsnCandidate Router(string address, double? rtt, bool preserved = false, bool unreachable = false) => new()
    {
        AsnNumber = 64500,
        AsnName = "Example",
        Method = DiscoveryMethod.DirectRouter,
        TargetId = $"transit-{address}",
        HopAddress = address,
        VerifiedRttMs = rtt,
        Enabled = !unreachable,
        Unreachable = unreachable,
        PreservedFromExisting = preserved,
    };

    [Fact]
    public void ADistantRouterAmongNearOnes_IsAnOutlier()
    {
        var far = Router("198.51.100.9", 212);
        var routers = new[] { Router("198.51.100.1", 18), Router("198.51.100.2", 22), Router("198.51.100.3", 31), far };

        UpstreamTracerService.OutlierTransitRouters(routers).Should().Equal(far);
    }

    [Fact]
    public void ARouterAlreadyMonitored_KeepsTheOperatorsChoice()
    {
        var routers = new[] { Router("198.51.100.1", 18), Router("198.51.100.2", 22), Router("198.51.100.3", 31), Router("198.51.100.9", 212, preserved: true) };

        UpstreamTracerService.OutlierTransitRouters(routers).Should().BeEmpty();
    }

    [Fact]
    public void ThreeTimesTheMedianButUnder100MsAbove_IsNotAnOutlier()
    {
        // 10 ms median: 45 ms is 4.5x but only 35 ms above.
        var routers = new[] { Router("198.51.100.1", 8), Router("198.51.100.2", 10), Router("198.51.100.3", 12), Router("198.51.100.9", 45) };

        UpstreamTracerService.OutlierTransitRouters(routers).Should().BeEmpty();
    }

    [Fact]
    public void FewerThanThreeTimedRouters_JudgesNothing()
    {
        var routers = new[] { Router("198.51.100.1", 20), Router("198.51.100.9", 250), Router("198.51.100.5", null, unreachable: true) };

        UpstreamTracerService.OutlierTransitRouters(routers).Should().BeEmpty();
    }

    [Fact]
    public void AnAllFarPath_HasNoOutliers()
    {
        // A satellite or overseas site: everything is slow, so nothing stands out.
        var routers = new[] { Router("198.51.100.1", 210), Router("198.51.100.2", 230), Router("198.51.100.3", 250) };

        UpstreamTracerService.OutlierTransitRouters(routers).Should().BeEmpty();
    }
}
