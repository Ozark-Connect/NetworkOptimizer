using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Core.Tests.Helpers;

public class DisplayFormatTests
{
    private static readonly DisplayFormat TwelveDayFirst = new(ClockStyle.TwelveHour, DateOrder.DayFirst);

    [Fact]
    public void Layered_WithNothingSet_IsTheFallback()
    {
        DisplayFormat.Layered(TwelveDayFirst).Should().Be(TwelveDayFirst);
        DisplayFormat.Layered(TwelveDayFirst, (null, null), (null, null)).Should().Be(TwelveDayFirst);
    }

    [Fact]
    public void Layered_TheFirstLayerToSetAHalfWins()
    {
        // A user who chose 24-hour, over an install default of 12-hour day-first.
        DisplayFormat.Layered(DisplayFormat.Fallback, ("24", null), ("12", "dmy"))
            .Should().Be(new DisplayFormat(ClockStyle.TwentyFourHour, DateOrder.DayFirst));
    }

    [Fact]
    public void Layered_AnUnreadableValueFallsThroughToTheNextLayer()
    {
        DisplayFormat.Layered(DisplayFormat.Fallback, ("13", "ymd"), ("12", "dmy")).Should().Be(TwelveDayFirst);
    }

    [Theory]
    [InlineData("24-mdy", ClockStyle.TwentyFourHour, DateOrder.MonthFirst)]
    [InlineData("12-dmy", ClockStyle.TwelveHour, DateOrder.DayFirst)]
    public void Code_RoundTrips(string code, ClockStyle clock, DateOrder order)
    {
        var format = new DisplayFormat(clock, order);

        format.Code.Should().Be(code);
        DisplayFormat.ParseCode(code).Should().Be(((ClockStyle?)clock, (DateOrder?)order));
    }
}
