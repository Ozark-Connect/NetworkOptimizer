using ApexCharts;
using FluentAssertions;
using Moq;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

/// <summary>
/// A C#-built time axis keeps ApexCharts' own time scale: no label formatter, its own local mode while the
/// viewer reads the browser's clock, wall-clock x only for another zone, and the viewer's format as
/// datetimeFormatter tokens no wider than the 24-hour ones.
/// </summary>
public class ChartTimeFormatTests
{
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    private static IDisplayTimeZone Viewer(DisplayFormat format, TimeZoneInfo zone, TimeZoneInfo? browser)
    {
        var display = new Mock<IDisplayTimeZone>();
        display.SetupGet(d => d.Format).Returns(format);
        display.SetupGet(d => d.Zone).Returns(zone);
        display.SetupGet(d => d.ZoneId).Returns(TimeZoneCatalog.ToIana(zone));
        display.SetupGet(d => d.BrowserZone).Returns(browser);
        display.Setup(d => d.ToLocal(It.IsAny<DateTime>()))
            .Returns<DateTime>(u => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(u, DateTimeKind.Utc), zone));
        display.Setup(d => d.ToUtc(It.IsAny<DateTime>()))
            .Returns<DateTime>(l => TimeZoneInfo.ConvertTimeToUtc(l, zone));
        return display.Object;
    }

    [Fact]
    public void ApplyTimeAxis_NeverSetsALabelFormatter()
    {
        var labels = new XAxisLabels { Formatter = "function (v) { return v; }" };

        ChartTimeFormat.ApplyTimeAxis(labels, Viewer(DisplayFormat.Fallback, Chicago, Chicago));

        labels.Formatter.Should().BeNull();
        labels.DatetimeFormatter.Should().NotBeNull();
        // display-time.js draws this axis's labels: ApexCharts' own crowd a wide chart.
        labels.Style!.CssClass.Should().Be(ChartTimeFormat.AxisClass);
    }

    [Fact]
    public void OnTheBrowsersOwnZone_TheAxisIsApexChartsLocalMode_OnRealInstants()
    {
        var viewer = Viewer(DisplayFormat.Fallback, Chicago, Chicago);
        var labels = new XAxisLabels { DatetimeUTC = true };
        var instant = new DateTime(2026, 11, 4, 17, 42, 0, DateTimeKind.Utc);

        ChartTimeFormat.ApplyTimeAxis(labels, viewer);

        labels.DatetimeUTC.Should().BeFalse();
        viewer.ChartsOnWallClock().Should().BeFalse();
        viewer.ToChart(instant).Should().Be(instant);
        viewer.FromChart(instant).Should().Be(instant);
    }

    [Theory]
    [InlineData(true)]   // a zone other than the browser's
    [InlineData(false)]  // the browser has not said
    public void OnAnotherZone_TheAxisDrawsTheViewersWallClock(bool browserSaid)
    {
        var viewer = Viewer(DisplayFormat.Fallback, Tokyo, browserSaid ? Chicago : null);
        var labels = new XAxisLabels();
        var instant = new DateTime(2026, 11, 3, 15, 0, 0, DateTimeKind.Utc);   // midnight Nov 4 in Tokyo

        ChartTimeFormat.ApplyTimeAxis(labels, viewer);

        labels.DatetimeUTC.Should().BeTrue();
        viewer.ToChart(instant).Should().Be(new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc));
        viewer.FromChart(viewer.ToChart(instant)).Should().Be(instant);
    }

    [Fact]
    public void ApplyTimeAxis_KeepsTheChartsOwnLabelSettings()
    {
        var style = new AxisLabelStyle { Colors = "#9ca3af" };
        var labels = new XAxisLabels { Style = style };

        ChartTimeFormat.ApplyTimeAxis(labels, Viewer(DisplayFormat.Fallback, Chicago, Chicago));

        labels.Style.Should().BeSameAs(style);
    }

    [Fact]
    public void TwentyFourHour_IsExactlyWhatTheChartsDrewBefore()
    {
        var tokens = ChartTimeFormat.DatetimeFormatterFor(DisplayFormat.Fallback);

        tokens.Hour.Should().Be("HH:mm");
        tokens.Day.Should().Be("MMM dd");
        // ApexCharts' own defaults.
        tokens.Year.Should().Be("yyyy");
        tokens.Month.Should().Be("MMM 'yy");
        tokens.Minute.Should().Be("HH:mm:ss");
        tokens.Second.Should().Be("HH:mm:ss");
    }

    [Fact]
    public void TwelveHour_IsNoWiderThanTwentyFourHour()
    {
        var tokens = ChartTimeFormat.DatetimeFormatterFor(new DisplayFormat(ClockStyle.TwelveHour, DateOrder.DayFirst));

        // "1 PM" beside "13:00", "10:45 AM" beside "10:45:00": a wider label makes ApexCharts drop ticks.
        tokens.Hour.Should().Be("h TT");
        tokens.Minute.Should().Be("h:mm TT");
        tokens.Second.Should().Be("h:mm:ss TT");
        tokens.Day.Should().Be("dd MMM");
    }

    [Theory]
    [InlineData(nameof(ChartTimeFormat.TooltipMinutes))]
    [InlineData(nameof(ChartTimeFormat.TooltipSeconds))]
    [InlineData(nameof(ChartTimeFormat.TooltipDay))]
    [InlineData(nameof(ChartTimeFormat.TooltipTime))]
    public void TooltipHeaders_ReadTheInstantByTheChartsOwnMode(string name)
    {
        var js = (string)typeof(ChartTimeFormat).GetField(name)!.GetValue(null)!;

        js.Should().Contain("window.noTime.instantOf(value, opts && opts.w)");
    }
}
