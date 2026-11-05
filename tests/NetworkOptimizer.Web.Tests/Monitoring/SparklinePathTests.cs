using FluentAssertions;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// The hero sparkline has to share the gauge's scale exactly, end on the live reading, and never
/// draw a reading that was not there.
/// </summary>
public class SparklinePathTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);
    private static readonly TimeSpan Gap = TimeSpan.FromMinutes(45);

    private static List<SparkPoint> Every15Min(params double[] values) =>
        values.Select((v, i) => new SparkPoint(Now - TimeSpan.FromMinutes(15 * (values.Length - i)), v)).ToList();

    [Fact]
    public void EndsOnTheLiveReading_AtTheGaugesOwnHeight()
    {
        var r = SparklinePath.Build(Every15Min(30, 32, 34), current: 40, Now, Day, 25, 45, Gap);

        r.EndX.Should().Be(100);
        r.EndY.Should().Be(100 - SignalGauge.Position(40, 25, 45));
        r.Line.Should().StartWith("M").And.EndWith("100,25");
    }

    [Fact]
    public void ALanding_KeepsTheLastShareOfTheWidthForTheReachToTheLiveReading()
    {
        var r = SparklinePath.Build(Every15Min(62, 62), current: 55, Now, Day, 0, 100, Gap, landing: 6);

        // The last mean (15 minutes old) sits at 93 of the 94 history keeps, not at 99; the line then
        // reaches the live reading early in the landing and holds it flat to 100.
        r.Line.Should().Contain(" 93.02,38").And.Contain(" 94.9,45").And.EndWith(" 100,45");
        r.EndX.Should().Be(100);
        r.EndY.Should().BeApproximately(45, 1e-9);
    }
    [Fact]
    public void TaperPolygon_StartsAtTheStrokeAndEndsAtTheGaugesReach()
    {
        var poly = SparklinePath.TaperPolygon(40, up: "9%", down: "9%", steps: 2);

        poly.Should().StartWith("polygon(0% calc(16px + 0.4 * (100% - 2 * 16px) - (0.875px + 0 * (9% - 0.875px)))");
        poly.Should().Contain("100% calc(16px + 0.4 * (100% - 2 * 16px) - (0.875px + 1 * (9% - 0.875px)))");
        poly.Should().EndWith("0% calc(16px + 0.4 * (100% - 2 * 16px) + (0.875px + 0 * (9% - 0.875px))))");
    }
    [Fact]
    public void NothingDraws_WithoutData()
    {
        var r = SparklinePath.Build(null, current: null, Now, Day, 0, 100, Gap);

        r.Line.Should().BeEmpty();
        r.Area.Should().BeEmpty();
        r.EndY.Should().BeNull();
    }

    [Fact]
    public void ALoneLiveReading_GivesTheDotButNoLine()
    {
        var r = SparklinePath.Build(null, current: 50, Now, Day, 0, 100, Gap);

        r.Line.Should().BeEmpty();
        r.EndY.Should().Be(50);
    }

    [Fact]
    public void AnOutage_BreaksTheLineInsteadOfBridgingIt()
    {
        var points = new List<SparkPoint>
        {
            new(Now.AddHours(-6), 50), new(Now.AddHours(-5.75), 52),
            new(Now.AddHours(-2), 60), new(Now.AddHours(-1.75), 61),
        };

        var r = SparklinePath.Build(points, current: null, Now, Day, 0, 100, Gap);

        r.Line.Count(c => c == 'M').Should().Be(2);
        r.Area.Count(c => c == 'Z').Should().Be(2);
    }

    [Fact]
    public void PointsOutsideTheWindow_AreDropped()
    {
        var points = new List<SparkPoint> { new(Now.AddHours(-30), 10), new(Now.AddHours(-1), 50) };

        var r = SparklinePath.Build(points, current: 50, Now, Day, 0, 100, Gap);

        r.Line.Should().NotContain("90");
    }

    [Fact]
    public void TheCurveNeverOvershootsTheBox()
    {
        // A step from the floor to the ceiling is where an unconstrained spline overshoots.
        var r = SparklinePath.Build(Every15Min(0, 0, 100, 100, 0), current: 0, Now, Day, 0, 100, Gap);

        var numbers = r.Line.Split('M', 'C', 'L', ' ', ',', 'Z')
            .Where(s => s.Length > 0)
            .Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture));
        numbers.Should().OnlyContain(n => n >= 0 && n <= 100);
    }

    [Fact]
    public void ReadingsOffTheScale_ClampToTheGaugesEnds()
    {
        var r = SparklinePath.Build(Every15Min(10, 60), current: 60, Now, Day, 25, 45, Gap);

        r.EndY.Should().Be(0);
    }

    [Fact]
    public void QualityStops_ChangeColorWhereTheBarsChangeCount()
    {
        SignalGauge.QualityStops().Select(s => s.Position).Should().Equal(0, 20, 20, 40, 40, 60, 60, 80, 80, 100);
        // Rescaled to a 50-100 day, the steps stay at the same qualities: 60 and 80 land at 20% and 60%.
        SignalGauge.QualityStops(50).Select(s => s.Position).Should().Equal(0, 0, 0, 0, 0, 20, 20, 60, 60, 100);
        SignalGauge.QualityStops()[4].Color.Should().Be("var(--signal-fair)", "a 55% reading is fair, like its bars");
    }

    [Fact]
    public void RsrpStops_ChangeColorAtTheBoxGrades()
    {
        var stops = SignalGauge.RsrpStops(-125, -65, fair: -100, good: -90);

        stops.Select(s => s.Position).Should().Equal(0, SignalGauge.Position(-100, -125, -65), SignalGauge.Position(-90, -125, -65), 100);
        stops[1].Color.Should().Be("var(--signal-fair)");
        stops[2].Color.Should().Be("var(--signal-good)");
    }

    [Fact]
    public void OpticalStops_AreTheTracksStops()
    {
        var stops = SignalGauge.OpticalStops(OpticalBands.Pon);

        stops.Should().HaveCount(9);
        stops[0].Color.Should().Be(stops[^1].Color, "red at both ends of a band track");
        stops.Select(s => s.Position).Should().BeInAscendingOrder();
    }
}
