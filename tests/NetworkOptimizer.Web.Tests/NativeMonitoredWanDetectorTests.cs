using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class NativeMonitoredWanDetectorTests
{
    [Theory]
    [InlineData("Starlink")]
    [InlineData("starlink")]
    [InlineData("STARLINK")]
    [InlineData("Starlink Backup")]
    [InlineData("WAN2 - Starlink")]
    public void IsUniFiNativeMonitored_MatchesStarlinkNames(string name)
    {
        NativeMonitoredWanDetector.IsUniFiNativeMonitored(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("Telekom")]
    [InlineData("Comcast")]
    [InlineData("Fiber")]
    [InlineData("")]
    [InlineData(null)]
    public void IsUniFiNativeMonitored_IgnoresOtherNames(string? name)
    {
        NativeMonitoredWanDetector.IsUniFiNativeMonitored(name).Should().BeFalse();
    }

    // The ISP signal is UniFi's own geo-IP classification (last_geo_info.isp_name), which
    // identifies the connection independently of the user-chosen WAN name. This is the
    // robust path: it fires even when the WAN is named something generic like "Internet 2 WAN2".
    [Theory]
    [InlineData("Starlink")]
    [InlineData("starlink")]
    [InlineData("SpaceX Starlink")]
    public void IsUniFiNativeMonitored_MatchesStarlinkIsp_EvenWhenNameDoesNotMatch(string isp)
    {
        NativeMonitoredWanDetector.IsUniFiNativeMonitored("Internet 2 WAN2", isp).Should().BeTrue();
    }

    [Theory]
    [InlineData("Deutsche Telekom")]
    [InlineData("Comcast Cable")]
    [InlineData("")]
    [InlineData(null)]
    public void IsUniFiNativeMonitored_IgnoresOtherIsps(string? isp)
    {
        NativeMonitoredWanDetector.IsUniFiNativeMonitored("Internet 2 WAN2", isp).Should().BeFalse();
    }

    [Fact]
    public void IsUniFiNativeMonitored_MatchesOnNameWhenIspIsMissing()
    {
        // Belt-and-suspenders: if last_geo_info hasn't populated yet (e.g. dish just booted
        // on CGNAT), the name marker still catches a WAN the user named "Starlink".
        NativeMonitoredWanDetector.IsUniFiNativeMonitored("Starlink WAN2", null).Should().BeTrue();
    }

    [Fact]
    public void IsUniFiNativeMonitored_FalseWhenNeitherNameNorIspMatch()
    {
        NativeMonitoredWanDetector.IsUniFiNativeMonitored("FTTH-Telekom", "Deutsche Telekom").Should().BeFalse();
    }
}
