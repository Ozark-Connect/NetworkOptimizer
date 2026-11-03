using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Core.Tests.Helpers;

public class DateTimeUtilitiesTests
{
    private static readonly DateTime January = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime July = new(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);

    // Every case reads the same on Windows and Linux, whose zone names differ.
    [Theory]
    [InlineData("America/Chicago", "CST", "CDT")]
    [InlineData("Australia/Sydney", "AEDT", "AEST")]
    [InlineData("Asia/Kolkata", "IST", "IST")]
    [InlineData("Etc/UTC", "UTC", "UTC")]
    public void A_settled_abbreviation_is_used(string id, string january, string july)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(id);

        DateTimeUtilities.ZoneAbbreviation(zone, January).Should().Be(january);
        DateTimeUtilities.ZoneAbbreviation(zone, July).Should().Be(july);
    }

    [Theory]
    [InlineData("Europe/Berlin", "UTC+01:00", "UTC+02:00")]   // "WEST" on Windows, "CEST" in winter on Linux
    [InlineData("Asia/Shanghai", "UTC+08:00", "UTC+08:00")]   // China Standard Time is not CST
    public void Initials_that_mislead_give_way_to_the_offset(string id, string january, string july)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(id);

        DateTimeUtilities.ZoneAbbreviation(zone, January).Should().Be(january);
        DateTimeUtilities.ZoneAbbreviation(zone, July).Should().Be(july);
    }

    [Fact]
    public void The_repeated_fall_back_hour_reads_each_side_correctly()
    {
        var chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

        // 01:30 happens twice in Chicago on 2026-11-01: first CDT, then CST.
        DateTimeUtilities.ZoneAbbreviation(chicago, new DateTime(2026, 11, 1, 6, 30, 0, DateTimeKind.Utc)).Should().Be("CDT");
        DateTimeUtilities.ZoneAbbreviation(chicago, new DateTime(2026, 11, 1, 7, 30, 0, DateTimeKind.Utc)).Should().Be("CST");
    }

    [Fact]
    public void An_unspecified_instant_is_read_as_utc()
    {
        var chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

        DateTimeUtilities.ZoneAbbreviation(chicago, new DateTime(2026, 7, 4, 17, 0, 0, DateTimeKind.Unspecified)).Should().Be("CDT");
        DateTimeUtilities.UtcOffsetLabel(TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"), January).Should().Be("UTC+05:30");
    }
}
