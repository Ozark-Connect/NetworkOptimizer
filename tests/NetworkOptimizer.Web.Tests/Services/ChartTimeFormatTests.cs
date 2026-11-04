using ApexCharts;
using FluentAssertions;
using Moq;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

/// <summary>
/// A C#-built time axis keeps ApexCharts' own time scale: no label formatter, UTC getters on wall-clock
/// x values, and the viewer's format as datetimeFormatter tokens.
/// </summary>
public class ChartTimeFormatTests
{
    private static IDisplayTimeZone Viewer(DisplayFormat format)
    {
        var display = new Mock<IDisplayTimeZone>();
        display.SetupGet(d => d.Format).Returns(format);
        return display.Object;
    }

    [Fact]
    public void ApplyTimeAxis_UsesApexChartsTimeScale_NeverALabelFormatter()
    {
        var labels = new XAxisLabels { DatetimeUTC = false, Formatter = "function (v) { return v; }" };

        ChartTimeFormat.ApplyTimeAxis(labels, Viewer(DisplayFormat.Fallback));

        labels.Formatter.Should().BeNull();
        labels.DatetimeUTC.Should().BeTrue();
        labels.DatetimeFormatter.Should().NotBeNull();
    }

    [Fact]
    public void ApplyTimeAxis_KeepsTheChartsOwnLabelSettings()
    {
        var style = new AxisLabelStyle { Colors = "#9ca3af" };
        var labels = new XAxisLabels { Style = style };

        ChartTimeFormat.ApplyTimeAxis(labels, Viewer(DisplayFormat.Fallback));

        labels.Style.Should().BeSameAs(style);
    }

    [Fact]
    public void Fallback_MatchesWhatTheChartsDrewBefore()
    {
        var tokens = ChartTimeFormat.DatetimeFormatterFor(DisplayFormat.Fallback);

        tokens.Hour.Should().Be("HH:mm");
        tokens.Day.Should().Be("MMM dd");
        // ApexCharts' own defaults, which no format setting changes.
        tokens.Year.Should().Be("yyyy");
        tokens.Month.Should().Be("MMM 'yy");
        tokens.Minute.Should().Be("HH:mm:ss");
        tokens.Second.Should().Be("HH:mm:ss");
    }

    [Fact]
    public void TwelveHourDayFirst_UsesApexChartsOwnTokens()
    {
        var tokens = ChartTimeFormat.DatetimeFormatterFor(new DisplayFormat(ClockStyle.TwelveHour, DateOrder.DayFirst));

        tokens.Hour.Should().Be("h:mm TT");
        tokens.Minute.Should().Be("h:mm:ss TT");
        tokens.Second.Should().Be("h:mm:ss TT");
        tokens.Day.Should().Be("dd MMM");
    }

    [Theory]
    [InlineData(nameof(ChartTimeFormat.TooltipMinutes))]
    [InlineData(nameof(ChartTimeFormat.TooltipSeconds))]
    [InlineData(nameof(ChartTimeFormat.TooltipDay))]
    [InlineData(nameof(ChartTimeFormat.TooltipTime))]
    public void TooltipHeaders_ReadTheWallClockXBackAsAnInstant(string name)
    {
        var js = (string)typeof(ChartTimeFormat).GetField(name)!.GetValue(null)!;

        js.Should().Contain("window.noTime.fromChart(value)");
    }
}
