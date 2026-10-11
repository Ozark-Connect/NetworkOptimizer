using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>Growth of a cumulative counter across readings, as the Dashboard 24-hour error counts use it.</summary>
public class CounterIncrementsTests
{
    [Fact]
    public void Total_SumsRisesAcrossReadings()
    {
        CounterIncrements.Total(new long?[] { 100, 105, 105, 120 }).Should().Be(20);
    }

    [Fact]
    public void Total_ResetAddsNothingAndCountsOnFromTheNewValue()
    {
        // 100 -> 110 (+10), reboot to 2 (+0), 2 -> 7 (+5)
        CounterIncrements.Total(new long?[] { 100, 110, 2, 7 }).Should().Be(15);
    }

    [Fact]
    public void Total_SkipsMissingReadings()
    {
        CounterIncrements.Total(new long?[] { 10, null, 14, null }).Should().Be(4);
    }

    [Fact]
    public void Total_IsZeroForAFlatCounter()
    {
        CounterIncrements.Total(new long?[] { 5_000_000, 5_000_000 }).Should().Be(0);
    }

    [Theory]
    [InlineData(new long[0])]
    [InlineData(new long[] { 42 })]
    public void Total_IsNullWithoutTwoReadings(long[] readings)
    {
        CounterIncrements.Total(readings.Select(r => (long?)r)).Should().BeNull();
    }

    [Fact]
    public void Total_IsNullWhenEveryReadingIsMissing()
    {
        CounterIncrements.Total(new long?[] { null, null }).Should().BeNull();
    }
}
