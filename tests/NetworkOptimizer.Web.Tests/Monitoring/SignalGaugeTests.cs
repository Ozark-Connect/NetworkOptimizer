using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// The gauges have to agree with the grade the reading is given, and optical Rx
/// has to run back to red above its band - an overdriven receiver is a fault, not
/// a good reading.
/// </summary>
public class SignalGaugeTests
{
    [Theory]
    [InlineData(25, 0)]
    [InlineData(35, 50)]
    [InlineData(45, 100)]
    [InlineData(10, 0)]    // clamped
    [InlineData(60, 100)]  // clamped
    public void Position_MapsOntoTheTrackAndClamps(double value, double expected)
    {
        SignalGauge.Position(value, 25, 45).Should().Be(expected);
    }

    [Fact]
    public void Position_IsZeroForADegenerateDomain()
    {
        SignalGauge.Position(5, 10, 10).Should().Be(0);
    }

    [Fact]
    public void OpticalTrack_EndsRedAtBothEnds()
    {
        var track = SignalGauge.OpticalTrack(OpticalBands.Pon);

        track.Should().StartWith("linear-gradient(to top, var(--signal-poor) 0%");
        track.Should().EndWith("var(--signal-poor) 100%)");
    }

    [Fact]
    public void SnrTrack_RisesToExcellentAndStaysThere()
    {
        var track = SignalGauge.SnrTrack(25, 45, fair: 30, good: 33, excellent: 36);

        track.Should().StartWith("linear-gradient(to top, var(--signal-poor) 0%");
        track.Should().EndWith("var(--signal-excellent) 100%)");
    }

    [Theory]
    [InlineData(-19.5, "signal-excellent")]
    [InlineData(-24, "signal-good")]
    [InlineData(-27, "signal-fair")]
    [InlineData(-31, "signal-poor")]
    [InlineData(-2, "signal-poor")]   // overdriven, not "great"
    public void PonBands_GradeBothDirections(double rx, string expected)
    {
        OpticalBands.Pon.ClassFor(rx).Should().Be(expected);
    }

    [Theory]
    [InlineData(-4.5, "signal-excellent")]
    [InlineData(-9, "signal-good")]
    [InlineData(-13, "signal-fair")]
    [InlineData(3, "signal-poor")]
    public void ActiveEthernetBands_UseTheirOwnRange(double rx, string expected)
    {
        OpticalBands.ActiveEthernet.ClassFor(rx).Should().Be(expected);
    }

    [Fact]
    public void PonPeaksWhereARealLinkRuns_NotAtTheBandMidpoint()
    {
        // The midpoint of -22..-8 is -15 dBm, which only shows up on an unusually
        // low split ratio. The peak is stated so the gauge tops out where a normal
        // link actually sits.
        OpticalBands.Pon.Peak.Should().Be(-18);
        OpticalBands.Pon.Peak.Should()
            .BeLessThan((OpticalBands.Pon.ExcellentLow + OpticalBands.Pon.ExcellentHigh) / 2);
    }

    [Fact]
    public void WindowEdges_LeaveALitBandOnTheReading()
    {
        var (above, below) = SignalGauge.WindowEdges(50, halfHeight: 9);

        above.Should().Be(41);
        below.Should().Be(41);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void WindowEdges_ClampAtTheEndsOfTheTrack(double position)
    {
        var (above, below) = SignalGauge.WindowEdges(position);

        above.Should().BeGreaterThanOrEqualTo(0);
        below.Should().BeGreaterThanOrEqualTo(0);
        (above + below).Should().BeLessThan(100);
    }

    [Fact]
    public void AGoodButLowReadingDoesNotLightTheBadEndOfTheTrack()
    {
        // -22.37 dBm grades good. Lighting from the bottom would show everything
        // below it - the fair, weak and poor stretches - which reads as alarming.
        var bands = OpticalBands.Pon;
        var pos = SignalGauge.Position(-22.37, bands.DomainLow, bands.DomainHigh);
        var (above, below) = SignalGauge.WindowEdges(pos);

        bands.ClassFor(-22.37).Should().Be("signal-good");
        below.Should().BeGreaterThan(10, "the poor end stays dimmed");
        above.Should().BeGreaterThan(10, "the overdriven end stays dimmed too");
    }

    [Fact]
    public void ClassFor_IsEmptyWithoutAReading()
    {
        OpticalBands.Pon.ClassFor(null).Should().BeEmpty();
    }

    [Fact]
    public void AnOverdrivenReceiverSitsHighOnTheTrackButInTheRed()
    {
        // The whole reason the optical track diverges: -2 dBm is near the top of
        // the domain, where a rising gauge would paint it green.
        var bands = OpticalBands.Pon;
        var pos = SignalGauge.Position(-2, bands.DomainLow, bands.DomainHigh);

        pos.Should().BeGreaterThan(90);
        bands.ClassFor(-2).Should().Be("signal-poor");
    }
}
