using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class StarlinkWanDetectorTests
{
    [Theory]
    [InlineData("Starlink")]
    [InlineData("starlink")]
    [InlineData("STARLINK")]
    [InlineData("Starlink Backup")]
    [InlineData("WAN2 - Starlink")]
    public void IsStarlinkWan_MatchesStarlinkNames(string name)
    {
        StarlinkWanDetector.IsStarlinkWan(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("Telekom")]
    [InlineData("Comcast")]
    [InlineData("Fiber")]
    [InlineData("")]
    [InlineData(null)]
    public void IsStarlinkWan_IgnoresOtherNames(string? name)
    {
        StarlinkWanDetector.IsStarlinkWan(name).Should().BeFalse();
    }

    // The ISP signal is UniFi's own geo-IP classification (last_geo_info.isp_name), which
    // identifies the connection independently of the user-chosen WAN name. This is the
    // robust path: it fires even when the WAN is named something generic like "Internet 2 WAN2".
    [Theory]
    [InlineData("Starlink")]
    [InlineData("starlink")]
    [InlineData("SpaceX Starlink")]
    public void IsStarlinkWan_MatchesStarlinkIsp_EvenWhenNameDoesNotMatch(string isp)
    {
        StarlinkWanDetector.IsStarlinkWan("Internet 2 WAN2", isp).Should().BeTrue();
    }

    [Theory]
    [InlineData("Deutsche Telekom")]
    [InlineData("Comcast Cable")]
    [InlineData("")]
    [InlineData(null)]
    public void IsStarlinkWan_IgnoresOtherIsps(string? isp)
    {
        StarlinkWanDetector.IsStarlinkWan("Internet 2 WAN2", isp).Should().BeFalse();
    }

    [Fact]
    public void IsStarlinkWan_MatchesOnNameWhenIspIsMissing()
    {
        // Belt-and-suspenders: if last_geo_info hasn't populated yet (e.g. dish just booted
        // on CGNAT), the name marker still catches a WAN the user named "Starlink".
        StarlinkWanDetector.IsStarlinkWan("Starlink WAN2", null).Should().BeTrue();
    }

    [Fact]
    public void IsStarlinkWan_FalseWhenNeitherNameNorIspMatch()
    {
        StarlinkWanDetector.IsStarlinkWan("FTTH-Telekom", "Deutsche Telekom").Should().BeFalse();
    }
}
