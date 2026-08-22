using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Core.Tests;

public class TemperatureScaleTests
{
    [Theory]
    [InlineData(48000, 48)]          // UXG-Lite millidegrees
    [InlineData(62500, 62.5)]
    [InlineData(48, 48)]             // already Celsius
    [InlineData(199.9, 199.9)]
    [InlineData(200, 200)]           // boundary stays as-is
    [InlineData(200.1, 0.2001)]
    [InlineData(0, 0)]
    [InlineData(-5000, -5)]          // sub-zero millidegrees
    [InlineData(-5, -5)]
    public void NormalizeCelsius_ScalesOnlyImplausibleReadings(double input, double expected)
    {
        TemperatureScale.NormalizeCelsius(input).Should().BeApproximately(expected, 0.0001);
    }

    [Fact]
    public void NormalizeCelsius_PassesNullThrough()
    {
        TemperatureScale.NormalizeCelsius((double?)null).Should().BeNull();
        TemperatureScale.NormalizeCelsius((double?)48000).Should().BeApproximately(48, 0.0001);
    }
}
