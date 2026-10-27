using FluentAssertions;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

public class ClientsLaneTests
{
    private static readonly DateTime T = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ClientsLane_StacksBandsInOrder_TotalIsTheTop()
    {
        var lane = SiteMetricsReader.ClientsLane(
        [
            (T, "5ghz", 10), (T, "2.4ghz", 3),
            (T.AddMinutes(15), "2.4ghz", 4), (T.AddMinutes(15), "5ghz", 12), (T.AddMinutes(15), "6ghz", 2),
        ]);

        lane.Stack.Select(l => l.Name).Should().Equal("2.4 GHz", "5 GHz", "6 GHz");
        lane.Stack[1].Points[0].Should().Be((T, 3.0, 13.0));
        lane.Stack[2].Points[0].Should().Be((T, 13.0, 13.0));
        lane.Stack.Select(l => l.Latest).Should().Equal(4, 12, 2);
        lane.SeriesA.Select(p => p.Value).Should().Equal(13.0, 18.0);
    }

    [Fact]
    public void ClientsLane_HoldsTheLastCountToTheEnd()
    {
        var end = T.AddHours(20);
        var lane = SiteMetricsReader.ClientsLane([(T, "5ghz", 7), (T, "2.4ghz", 2)], end);
        lane.SeriesA.Should().Equal((T, 9.0), (end, 9.0));
        lane.Stack.Select(l => l.Latest).Should().Equal(2, 7);
    }

    [Fact]
    public void ClientsLane_FillsWindowsWithNoClientsAsZero()
    {
        var step = TimeSpan.FromMinutes(15);
        var end = T.AddMinutes(60);
        var lane = SiteMetricsReader.ClientsLane([(T, "5ghz", 4), (T.AddMinutes(30), "5ghz", 2)], end, step);
        lane.SeriesA.Select(p => p.Value).Should().Equal(4.0, 0.0, 2.0, 0.0, 0.0);
        lane.Stack.Single().Latest.Should().Be(0);
    }

    [Fact]
    public void ClientsLane_FillsLeadingWindowsBackToTheRangeStart()
    {
        var day = TimeSpan.FromDays(1);
        var lane = SiteMetricsReader.ClientsLane([(T.AddDays(3), "5ghz", 5)], T.AddDays(4), day, T.AddHours(-6));
        lane.SeriesA.Select(p => p.Time).Should().Equal(T.AddDays(-1), T, T.AddDays(1), T.AddDays(2), T.AddDays(3), T.AddDays(4));
        lane.SeriesA.Select(p => p.Value).Should().Equal(0.0, 0.0, 0.0, 0.0, 5.0, 5.0);
    }

    [Fact]
    public void ClientsLane_UnalignedRangeStart_FillsNoZeroBetweenWindows()
    {
        // Flux stamps the first window at the range start; later windows sit on the epoch grid.
        var step = TimeSpan.FromSeconds(288);
        var start = T.AddSeconds(100);
        var end = T.AddSeconds(100 + 288 * 3);
        var lane = SiteMetricsReader.ClientsLane(
            [(start, "5ghz", 8), (T.AddSeconds(288), "5ghz", 8), (T.AddSeconds(576), "5ghz", 9), (T.AddSeconds(864), "5ghz", 9)],
            end, step, start);
        lane.SeriesA.Select(p => p.Time).Should().Equal(T, T.AddSeconds(288), T.AddSeconds(576), T.AddSeconds(864), end);
        lane.SeriesA.Select(p => p.Value).Should().Equal(8.0, 8.0, 9.0, 9.0, 9.0);
    }

    [Fact]
    public void PhyLane_BreaksTheLineWhereABandHadNoClients()
    {
        var step = TimeSpan.FromMinutes(15);
        var lane = SiteMetricsReader.PhyLane([(T, "5ghz", 800_000), (T.AddMinutes(15), "5ghz", 900_000), (T.AddMinutes(60), "5ghz", 1_200_000)], step);
        lane.Lines.Single().Series.Should().Equal((T, 800.0), (T.AddMinutes(15), 900.0), (T.AddMinutes(30), null), (T.AddMinutes(60), 1200.0));
    }

    [Theory]
    [InlineData(24.4, "24")]
    [InlineData(9.34, "9.3")]
    [InlineData(0.854, "0.85")]
    public void TwoSignificant_KeepsTwoDigits(double v, string expected) =>
        SiteMetricsReader.TwoSignificant(v).Should().Be(expected);

    [Fact]
    public void ClientsLane_DropsABandWithNoClients()
    {
        var lane = SiteMetricsReader.ClientsLane([(T, "5ghz", 7)]);
        lane.Stack.Should().ContainSingle(l => l.Name == "5 GHz");
    }
}
