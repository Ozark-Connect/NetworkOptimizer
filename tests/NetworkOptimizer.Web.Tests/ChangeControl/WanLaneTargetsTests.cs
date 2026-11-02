using FluentAssertions;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>The WAN lanes read the ISP and transit hops, falling back to the loss pool's anycast DNS only without them.</summary>
public class WanLaneTargetsTests
{
    private static readonly DateTime T = new(2026, 11, 1, 18, 0, 0, DateTimeKind.Utc);

    private static MonitoringInfluxClient.TargetLatencyMean Row(int minute, string type, double rtt) =>
        new(T.AddMinutes(minute), type + "-target", type, rtt, 0);

    [Fact]
    public void WithAnIspHop_TheDnsTargetsAreLeftOut()
    {
        var rows = new[] { Row(0, "accessisp", 2.8), Row(0, "internetservice", 27.3), Row(5, "transit", 14.9), Row(5, "internetservice", 27.1) };

        SiteMetricsReader.IspHopsOrDns(rows).Select(r => r.TargetType).Should().BeEquivalentTo(["accessisp", "transit"]);
    }

    [Fact]
    public void WithoutAnyHop_TheDnsTargetsFillTheLanes()
    {
        var rows = new[] { Row(0, "internetservice", 27.3), Row(5, "internetservice", 27.1) };

        SiteMetricsReader.IspHopsOrDns(rows).Should().HaveCount(2);
    }

    [Fact]
    public void AHopInPartOfTheRange_StillLeavesTheDnsTargetsOut()
    {
        // Decided over the whole range: a window where the hop has no data does not jump to the DNS RTT.
        var rows = new[] { Row(0, "internetservice", 27.3), Row(5, "accessisp", 2.8), Row(5, "internetservice", 27.1) };

        SiteMetricsReader.IspHopsOrDns(rows).Should().ContainSingle().Which.TargetType.Should().Be("accessisp");
    }

    [Fact]
    public void NoRows_StaysEmpty() => SiteMetricsReader.IspHopsOrDns([]).Should().BeEmpty();
}
