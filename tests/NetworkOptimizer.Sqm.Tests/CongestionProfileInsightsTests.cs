using FluentAssertions;
using NetworkOptimizer.Sqm.Models;
using Xunit;

namespace NetworkOptimizer.Sqm.Tests;

public class CongestionProfileInsightsTests
{
    private static LearnedCongestionProfile Profile(Func<int, int, double> multiplier)
    {
        var p = new LearnedCongestionProfile();
        for (var d = 0; d < 7; d++)
            for (var h = 0; h < 24; h++)
                p.DownloadMultipliers[LearnedCongestionProfile.SlotIndex(d, h)] = multiplier(d, h);
        return p;
    }

    [Fact]
    public void NamesTheSlowBand_InHoursAndPercentOfBest()
    {
        var learned = Profile((d, h) => h is >= 19 and <= 22 ? 0.7 : 1.0);

        var insight = CongestionProfileInsights.Describe(learned, ConnectionType.CellularHome)!;

        insight.StartHour.Should().Be(19);
        insight.EndHour.Should().Be(22);
        insight.Depth.Should().BeApproximately(0.3, 0.001);
        insight.SlowestSentence.Should().Be("evenings (19:00 to 22:00), about 70% of your best hour");
    }

    [Fact]
    public void LabelsWeekdaysWhenTheDipIsAWeekdayThing()
    {
        var learned = Profile((d, h) => h is >= 19 and <= 22 && d < 5 ? 0.6 : 1.0);

        CongestionProfileInsights.Describe(learned, ConnectionType.CellularHome)!.SlowestLabel.Should().Be("weekday evenings");
    }

    [Fact]
    public void LabelsWeekendsWhenTheDipIsAWeekendThing()
    {
        var learned = Profile((d, h) => h is >= 13 and <= 16 && d >= 5 ? 0.6 : 1.0);

        CongestionProfileInsights.Describe(learned, ConnectionType.Starlink)!.SlowestLabel.Should().Be("weekend afternoons");
    }

    [Fact]
    public void ABandAcrossMidnight_ReadsAsOvernight()
    {
        var learned = Profile((d, h) => h is 23 or 0 or 1 or 2 ? 0.75 : 1.0);

        var insight = CongestionProfileInsights.Describe(learned, ConnectionType.CellularHome)!;

        insight.StartHour.Should().Be(23);
        insight.EndHour.Should().Be(2);
        insight.SlowestLabel.Should().Be("overnight");
    }

    [Fact]
    public void ComparesAgainstTheConnectionTypesDefaultOverTheSameHours()
    {
        // Cellular's built-in evening dip is well under 40%; a 60% dip reads as deeper.
        var deeper = Profile((d, h) => h is >= 19 and <= 22 ? 0.4 : 1.0);
        CongestionProfileInsights.Describe(deeper, ConnectionType.CellularHome)!.Comparison
            .Should().Be(CongestionProfileInsights.Comparison.Deeper);

        // GPON's default is nearly flat; a 30% learned dip is deeper than it expects.
        var gpon = Profile((d, h) => h is >= 19 and <= 22 ? 0.7 : 1.0);
        var g = CongestionProfileInsights.Describe(gpon, ConnectionType.Gpon)!;
        g.Comparison.Should().Be(CongestionProfileInsights.Comparison.Deeper);
        g.ComparisonSentence("Fiber (GPON)").Should().StartWith("dips deeper than the Fiber (GPON) default expects");
    }

    [Fact]
    public void AFlatLine_SaysTheDefaultWasShapingHarderThanNeeded()
    {
        var flat = Profile((d, h) => h == 20 ? 0.97 : 1.0);

        var insight = CongestionProfileInsights.Describe(flat, ConnectionType.CellularHome)!;

        insight.Comparison.Should().Be(CongestionProfileInsights.Comparison.Flat);
        insight.ComparisonSentence("Fixed LTE/5G").Should().Contain("barely varies");
        // No band is named on a flat line: it would wrap most of the clock.
        insight.SlowestSentence.Should().Be("no slow band yet, your line stays within about 3% all day");
    }

    [Fact]
    public void AFlatLineOnAFlatDefault_HasNothingToSay()
    {
        var flat = Profile((d, h) => 1.0);

        CongestionProfileInsights.Describe(flat, ConnectionType.XgsPon).Should().BeNull();
    }
}
