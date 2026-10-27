using FluentAssertions;
using NetworkOptimizer.Alerts.Delivery;
using Xunit;

namespace NetworkOptimizer.Alerts.Tests.Delivery;

public class TimestampFormatterTests
{
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    [Fact]
    public void Formats_on_the_given_zone_with_its_abbreviation()
    {
        var utc = new DateTime(2026, 2, 24, 20, 42, 50, DateTimeKind.Utc);

        TimestampFormatter.FormatLocal(utc, Chicago).Should().Be("2026-02-24 14:42:50 CST");
        TimestampFormatter.FormatLocalShort(utc, Chicago).Should().Be("14:42 CST");
    }

    [Fact]
    public void Reads_an_unspecified_stored_time_as_utc()
    {
        var stored = new DateTime(2026, 7, 4, 17, 0, 0, DateTimeKind.Unspecified);

        TimestampFormatter.FormatLocalShort(stored, Chicago).Should().Be("12:00 CDT");
        TimestampFormatter.FormatLocalShort(stored, TimeZoneInfo.Utc).Should().Be("17:00 UTC");
    }
}
