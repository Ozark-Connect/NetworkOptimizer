using FluentAssertions;
using NetworkOptimizer.Alerts.Delivery;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Alerts.Tests.Delivery;

public class TimestampFormatterTests
{
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    private static readonly DisplayFormat TwelveDayFirst = new(ClockStyle.TwelveHour, DateOrder.DayFirst);

    [Fact]
    public void Formats_on_the_given_zone_with_its_abbreviation()
    {
        var utc = new DateTime(2026, 2, 24, 20, 42, 50, DateTimeKind.Utc);

        TimestampFormatter.FormatLocal(utc, Chicago, DisplayFormat.Fallback).Should().Be("Feb 24, 2026, 14:42:50 CST");
        TimestampFormatter.FormatLocalShort(utc, Chicago, DisplayFormat.Fallback).Should().Be("14:42 CST");
    }

    [Fact]
    public void Follows_the_notification_format()
    {
        var utc = new DateTime(2026, 2, 24, 20, 42, 50, DateTimeKind.Utc);

        TimestampFormatter.FormatLocal(utc, Chicago, TwelveDayFirst).Should().Be("24 Feb 2026, 2:42:50 PM CST");
        TimestampFormatter.FormatLocalShort(utc, Chicago, TwelveDayFirst).Should().Be("2:42 PM CST");
    }

    [Fact]
    public void Reads_an_unspecified_stored_time_as_utc()
    {
        var stored = new DateTime(2026, 7, 4, 17, 0, 0, DateTimeKind.Unspecified);

        TimestampFormatter.FormatLocalShort(stored, Chicago, DisplayFormat.Fallback).Should().Be("12:00 CDT");
        TimestampFormatter.FormatLocalShort(stored, TimeZoneInfo.Utc, DisplayFormat.Fallback).Should().Be("17:00 UTC");
    }
}
