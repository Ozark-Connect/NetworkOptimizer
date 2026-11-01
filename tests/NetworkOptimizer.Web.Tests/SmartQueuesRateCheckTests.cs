using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class SmartQueuesRateCheckTests
{
    [Fact]
    public void RatesAtOrAboveAdaptiveSqms_RaiseNothing()
    {
        SmartQueuesRateCheck.Warnings("WAN1", 1078, 990, 1078, 990).Should().BeEmpty("equal is enough");
        SmartQueuesRateCheck.Warnings("WAN1", 1200, 1000, 1078, 990).Should().BeEmpty();
    }

    [Fact]
    public void ALowerDownloadRate_Warns_WithBothRates()
    {
        var warnings = SmartQueuesRateCheck.Warnings("WAN1", 1000, 990, 1078, 990);

        warnings.Should().ContainSingle();
        warnings[0].Should().Contain("WAN1").And.Contain("download rate (1000 Mbps)").And.Contain("1078 Mbps or more");
    }

    [Fact]
    public void ALowerUploadRate_Warns_EvenWhenDownloadIsFine()
    {
        var warnings = SmartQueuesRateCheck.Warnings("WAN1", 1078, 900, 1078, 990);

        warnings.Should().ContainSingle().Which.Should().Contain("upload rate (900 Mbps)").And.Contain("990 Mbps or more");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, 0)]
    public void ADirectionUniFiReportsNoRateFor_IsSkipped(int? down, int? up)
    {
        SmartQueuesRateCheck.Warnings("WAN1", down, up, 1078, 990).Should().BeEmpty();
    }

    [Theory]
    [InlineData(1078, null, 1078)]
    [InlineData(1078, 0, 1078)]
    [InlineData(1078, 1000, 980)]
    [InlineData(900, 10000, 900)]
    public void MaxShapedDownload_IsTheConfiguredMax_CappedBelowAKnownLinkSpeed(int max, int? link, int expected)
    {
        SmartQueuesRateCheck.MaxShapedDownloadMbps(max, link).Should().Be(expected);
    }
}
