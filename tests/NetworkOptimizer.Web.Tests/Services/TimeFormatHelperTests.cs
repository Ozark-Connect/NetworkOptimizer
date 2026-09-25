using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

public class TimeFormatHelperTests
{
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
        TimeFormatHelper.FormatRelativeTimeTerse(Now.AddSeconds(offsetSeconds), Now).Should().Be(expected);
    }
}
