using FluentAssertions;
using NetworkOptimizer.Web.Components.Shared.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>Timeline lane helpers: loaded-run shading and the bridged loaded line.</summary>
public class CcFormatTests
{
    private static readonly DateTime T = new(2026, 10, 31, 16, 0, 0, DateTimeKind.Utc);

    private static List<(DateTime Time, double? Value)> Series(params double?[] values) =>
        values.Select((v, i) => (T.AddMinutes(5 * i), v)).ToList();

    [Fact]
    public void AreasUnder_ClosesEachLineDownToTheBaseline()
    {
        var areas = CcFormat.AreasUnder(Series(null, 5, 10, null), T, T.AddMinutes(15), 10);

        areas.Should().ContainSingle().Which.Should().EndWith("66.67,96 33.33,96");
    }

    [Fact]
    public void Bridged_JoinsEachLoadedRunToTheIdlePointsEitherSide()
    {
        var idle = Series(1, 1.1, null, null, 1.2, 1.3);
        var loaded = Series(null, null, 5, 6, null, null);

        CcFormat.Bridged(idle, loaded).Select(p => p.Value).Should().Equal(null, 1.1, 5, 6, 1.2, null);
    }

    [Fact]
    public void Bridged_ALoneLoadedPointBecomesADrawableLine()
    {
        var idle = Series(1, null, 1);
        var loaded = Series(null, 7, null);

        var bridged = CcFormat.Bridged(idle, loaded);

        CcFormat.Polylines(bridged, T, T.AddMinutes(10), 10).Should().ContainSingle();
    }
}
