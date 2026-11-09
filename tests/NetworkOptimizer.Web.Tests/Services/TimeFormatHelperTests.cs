using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

public class TimeFormatHelperTests
{
    [Fact]
    public void A_twelve_hour_time_keeps_its_designator_under_any_server_culture()
    {
        // German has no AM/PM designator; the viewer's format must not lose it on such a server.
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            var us = new DisplayFormat(ClockStyle.TwelveHour, DateOrder.MonthFirst);
            TimeFormatHelper.FormatLocalDateTime(new DateTime(2026, 11, 3, 14, 5, 0, DateTimeKind.Utc), TimeZoneInfo.Utc, us)
                .Should().Be("Nov 3, 2:05 PM");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    private static readonly DateTime Now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(-30, "Just now")]
    [InlineData(-5 * 60, "5m ago")]
    [InlineData(-3 * 3600, "3h ago")]
    [InlineData(-2 * 86400, "2d ago")]
    [InlineData(-8 * 86400, "Sep 17, 2026")]
    [InlineData(30, "Any moment")]
    [InlineData(5 * 60, "in 5m")]
    [InlineData(3 * 3600 + 10 * 60, "in 3h 10m")]
    [InlineData(2 * 86400, "in 2d")]
    public void FormatRelativeTimeTerse_ReturnsExpected(int offsetSeconds, string expected)
    {
        TimeFormatHelper.FormatRelativeTimeTerse(Now.AddSeconds(offsetSeconds), TimeZoneInfo.Utc, DisplayFormat.Fallback, Now).Should().Be(expected);
    }

    [Fact]
    public void FormatRelativeTimeTerse_PastAWeek_ShowsTheDateOnTheGivenClock()
    {
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        // 20:00 UTC on Sep 17 is already Sep 18 in Tokyo.
        var lateEvening = new DateTime(2026, 9, 17, 20, 0, 0, DateTimeKind.Utc);

        TimeFormatHelper.FormatRelativeTimeTerse(lateEvening, tokyo, DisplayFormat.Fallback, Now).Should().Be("Sep 18, 2026");
    }

    [Theory]
    [InlineData(600, "10 minutes")]
    [InlineData(60, "1 minute")]
    [InlineData(3600, "1 hour")]
    [InlineData(5400, "1 hour, 30 minutes")]
    [InlineData(86400, "1 day")]
    [InlineData(95400, "1 day, 2 hours, 30 minutes")]
    [InlineData(45, "45 seconds")]
    [InlineData(0, "0 seconds")]
    public void FormatDurationExact_StatesEveryNonZeroUnit(int seconds, string expected)
    {
        TimeFormatHelper.FormatDurationExact(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
    }
}
