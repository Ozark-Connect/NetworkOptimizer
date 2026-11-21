using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// The result links alerts store and the Dashboard renders. Alert URLs are frozen into history, so the shape is fixed.
/// </summary>
public class SpeedTestLinksTests
{
    [Fact]
    public void WanResult_NamesTheWan()
    {
        SpeedTestLinks.WanResult(new Iperf3Result { Id = 42, WanNetworkGroup = "WAN2" })
            .Should().Be("/wan-speedtest?wan=wan2&result=42");
    }

    [Fact]
    public void WanResult_BondedTest_ListsEachWan()
    {
        SpeedTestLinks.WanResult(new Iperf3Result { Id = 7, WanNetworkGroup = "WAN+WAN2" })
            .Should().Be("/wan-speedtest?wan=wan%2Cwan2&result=7");
    }

    [Fact]
    public void WanResult_NoWan_ResultOnly()
    {
        SpeedTestLinks.WanResult(new Iperf3Result { Id = 3 }).Should().Be("/wan-speedtest?result=3");
    }

    [Fact]
    public void LanResult()
    {
        SpeedTestLinks.LanResult(9).Should().Be("/speedtest?result=9");
    }

    [Fact]
    public void ClientResult()
    {
        SpeedTestLinks.ClientResult(11).Should().Be("/client-speedtest?result=11");
    }
}
