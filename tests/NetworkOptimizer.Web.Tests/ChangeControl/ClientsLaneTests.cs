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
