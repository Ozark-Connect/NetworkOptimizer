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
}
