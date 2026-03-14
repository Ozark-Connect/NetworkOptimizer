using FluentAssertions;
using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class SignalClassificationTests
{
    // --- GetSignalClass with RadioBand ---

    [Theory]
    [InlineData(-45, RadioBand.Band5GHz, "signal-excellent")]
    [InlineData(-65, RadioBand.Band5GHz, "signal-good")]
    [InlineData(-74, RadioBand.Band5GHz, "signal-fair")]
    [InlineData(-82, RadioBand.Band5GHz, "signal-weak")]
    [InlineData(-90, RadioBand.Band5GHz, "signal-poor")]
    public void GetSignalClass_5GHz_CorrectClassification(int dbm, RadioBand band, string expected)
    {
        SignalClassification.GetSignalClass(dbm, band).Should().Be(expected);
    }

    [Theory]
    [InlineData(-45, RadioBand.Band2_4GHz, "signal-excellent")]
    [InlineData(-55, RadioBand.Band2_4GHz, "signal-good")]
    [InlineData(-65, RadioBand.Band2_4GHz, "signal-fair")]
    [InlineData(-72, RadioBand.Band2_4GHz, "signal-weak")]
    [InlineData(-80, RadioBand.Band2_4GHz, "signal-poor")]
    public void GetSignalClass_2_4GHz_CorrectClassification(int dbm, RadioBand band, string expected)
    {
        SignalClassification.GetSignalClass(dbm, band).Should().Be(expected);
    }

    [Theory]
    [InlineData(-60, RadioBand.Band6GHz, "signal-excellent")]
    [InlineData(-72, RadioBand.Band6GHz, "signal-good")]
    [InlineData(-84, RadioBand.Band6GHz, "signal-fair")]
    [InlineData(-90, RadioBand.Band6GHz, "signal-weak")]
    [InlineData(-95, RadioBand.Band6GHz, "signal-poor")]
    public void GetSignalClass_6GHz_CorrectClassification(int dbm, RadioBand band, string expected)
    {
        SignalClassification.GetSignalClass(dbm, band).Should().Be(expected);
    }

    // --- Same signal, different band ---

    [Fact]
    public void SameSignal_ClassifiedDifferentlyByBand()
    {
        // -75 dBm: weak on 2.4 GHz, fair on 5 GHz, good on 6 GHz
        SignalClassification.GetSignalClass(-75, RadioBand.Band2_4GHz).Should().Be("signal-weak");
        SignalClassification.GetSignalClass(-75, RadioBand.Band5GHz).Should().Be("signal-fair");
        SignalClassification.GetSignalClass(-75, RadioBand.Band6GHz).Should().Be("signal-good");
    }

    [Fact]
    public void SameSignal_Minus68_ClassifiedDifferentlyByBand()
    {
        // -68 dBm: weak on 2.4 GHz, good on 5 GHz, good on 6 GHz
        SignalClassification.GetSignalClass(-68, RadioBand.Band2_4GHz).Should().Be("signal-weak");
        SignalClassification.GetSignalClass(-68, RadioBand.Band5GHz).Should().Be("signal-good");
        SignalClassification.GetSignalClass(-68, RadioBand.Band6GHz).Should().Be("signal-good");
    }

    // --- Unknown band defaults to 5 GHz ---

    [Fact]
    public void UnknownBand_Uses5GHzThresholds()
    {
        SignalClassification.GetSignalClass(-65, RadioBand.Unknown)
            .Should().Be(SignalClassification.GetSignalClass(-65, RadioBand.Band5GHz));
    }

    // --- String band overload ---

    [Theory]
    [InlineData("ng", RadioBand.Band2_4GHz)]
    [InlineData("na", RadioBand.Band5GHz)]
    [InlineData("6e", RadioBand.Band6GHz)]
    public void StringBandOverload_MatchesEnumOverload(string bandStr, RadioBand band)
    {
        SignalClassification.GetSignalClass(-70, bandStr)
            .Should().Be(SignalClassification.GetSignalClass(-70, band));
    }

    [Fact]
    public void NullBandString_DefaultsTo5GHz()
    {
        SignalClassification.GetSignalClass(-70, (string?)null)
            .Should().Be(SignalClassification.GetSignalClass(-70, RadioBand.Band5GHz));
    }

    // --- Nullable signal overload ---

    [Fact]
    public void NullSignal_ReturnsEmptyString()
    {
        SignalClassification.GetSignalClass(null, RadioBand.Band5GHz).Should().Be("");
    }

    [Fact]
    public void NullableSignalWithValue_ReturnsCorrectClass()
    {
        SignalClassification.GetSignalClass((int?)-65, RadioBand.Band5GHz).Should().Be("signal-good");
    }

    // --- IsWeakSignal ---

    [Theory]
    [InlineData(-67, RadioBand.Band2_4GHz, false)]  // exactly at threshold = not weak
    [InlineData(-68, RadioBand.Band2_4GHz, true)]
    [InlineData(-78, RadioBand.Band5GHz, false)]
    [InlineData(-79, RadioBand.Band5GHz, true)]
    [InlineData(-87, RadioBand.Band6GHz, false)]
    [InlineData(-88, RadioBand.Band6GHz, true)]
    public void IsWeakSignal_BandAwareThresholds(int dbm, RadioBand band, bool expected)
    {
        SignalClassification.IsWeakSignal(dbm, band).Should().Be(expected);
    }

    [Fact]
    public void IsWeakSignal_SameSignalDifferentBands()
    {
        // -75 dBm is weak on 2.4 GHz but not on 5 GHz or 6 GHz
        SignalClassification.IsWeakSignal(-75, RadioBand.Band2_4GHz).Should().BeTrue();
        SignalClassification.IsWeakSignal(-75, RadioBand.Band5GHz).Should().BeFalse();
        SignalClassification.IsWeakSignal(-75, RadioBand.Band6GHz).Should().BeFalse();
    }

    // --- IsCriticalSignal ---

    [Theory]
    [InlineData(-75, RadioBand.Band2_4GHz, false)]
    [InlineData(-76, RadioBand.Band2_4GHz, true)]
    [InlineData(-85, RadioBand.Band5GHz, false)]
    [InlineData(-86, RadioBand.Band5GHz, true)]
    [InlineData(-92, RadioBand.Band6GHz, false)]
    [InlineData(-93, RadioBand.Band6GHz, true)]
    public void IsCriticalSignal_BandAwareThresholds(int dbm, RadioBand band, bool expected)
    {
        SignalClassification.IsCriticalSignal(dbm, band).Should().Be(expected);
    }

    // --- GetWeakThreshold ---

    [Theory]
    [InlineData(RadioBand.Band2_4GHz, -67)]
    [InlineData(RadioBand.Band5GHz, -78)]
    [InlineData(RadioBand.Band6GHz, -87)]
    public void GetWeakThreshold_ReturnsBandSpecificValues(RadioBand band, int expected)
    {
        SignalClassification.GetWeakThreshold(band).Should().Be(expected);
    }

    // --- Boundary values ---

    [Fact]
    public void BoundaryValues_ExactThresholds_5GHz()
    {
        SignalClassification.GetSignalClass(-60, RadioBand.Band5GHz).Should().Be("signal-excellent");
        SignalClassification.GetSignalClass(-61, RadioBand.Band5GHz).Should().Be("signal-good");
        SignalClassification.GetSignalClass(-70, RadioBand.Band5GHz).Should().Be("signal-good");
        SignalClassification.GetSignalClass(-71, RadioBand.Band5GHz).Should().Be("signal-fair");
        SignalClassification.GetSignalClass(-78, RadioBand.Band5GHz).Should().Be("signal-fair");
        SignalClassification.GetSignalClass(-79, RadioBand.Band5GHz).Should().Be("signal-weak");
        SignalClassification.GetSignalClass(-85, RadioBand.Band5GHz).Should().Be("signal-weak");
        SignalClassification.GetSignalClass(-86, RadioBand.Band5GHz).Should().Be("signal-poor");
    }
}
