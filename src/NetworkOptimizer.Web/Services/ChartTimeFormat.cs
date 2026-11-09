using ApexCharts;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Time axes and tooltip headers for a C#-built chart on the viewer's clock: its x values are
/// <see cref="DisplayTimeExtensions.ToChart"/>, and the strings are the JavaScript functions
/// Blazor-ApexCharts takes, reading window.noTime (display-time.js) the same as the module charts.
/// </summary>
public static class ChartTimeFormat
{
    /// <summary>
    /// Puts ApexCharts' own time scale on a time axis, in the viewer's format: it places every tick
    /// and day break, and only the text follows the viewer. Never set a label Formatter on a time axis:
    /// any formatter turns that scale off and ApexCharts spaces ticks evenly with no breaks.
    /// </summary>
    /// <param name="labels">The chart's x-axis labels.</param>
    /// <param name="displayTime">The viewer's clock and format.</param>
    public static void ApplyTimeAxis(XAxisLabels labels, IDisplayTimeZone displayTime)
    {
        // True reads the wall-clock x values with UTC getters; false is ApexCharts' own local mode.
        labels.DatetimeUTC = displayTime.ChartsOnWallClock();
        labels.DatetimeFormatter = DatetimeFormatterFor(displayTime.Format);
        labels.Formatter = null;
        // Opts the axis into display-time.js drawTimeAxis, which draws the labels ApexCharts' own would crowd.
        labels.Style ??= new AxisLabelStyle();
        labels.Style.CssClass = AxisClass;
    }

    /// <summary>The x-axis label class display-time.js drawTimeAxis draws the labels for.</summary>
    public const string AxisClass = "no-time-axis";

    /// <summary>
    /// ApexCharts' datetimeFormatter tokens for a format; year and month keep ApexCharts' defaults.
    /// The twin of display-time.js datetimeFormatter.
    /// </summary>
    /// <param name="format">The viewer's format.</param>
    public static DatetimeFormatter DatetimeFormatterFor(DisplayFormat format)
    {
        var day = format.Order switch { DateOrder.DayFirst => "dd MMM", DateOrder.YearFirst => "MM-dd", _ => "MMM dd" };
        // The 12-hour forms are no wider than the 24-hour ones ("1 PM" beside "13:00"), so ApexCharts
        // lays the axis out the same and its overlap check drops no labels.
        return format.Clock == ClockStyle.TwentyFourHour
            ? new DatetimeFormatter { Year = "yyyy", Month = "MMM 'yy", Day = day, Hour = "HH:mm", Minute = "HH:mm:ss", Second = "HH:mm:ss" }
            : new DatetimeFormatter { Year = "yyyy", Month = "MMM 'yy", Day = day, Hour = "h TT", Minute = "h:mm TT", Second = "h:mm:ss TT" };
    }

    // Each reads the instant back by the chart's own datetimeUTC flag (opts.w), which ApplyTimeAxis set.

    /// <summary>A tooltip header: date and time to the minute.</summary>
    public const string TooltipMinutes =
        "function (value, opts) { return window.noTime ? window.noTime.tooltipLabel(window.noTime.instantOf(value, opts && opts.w), false) : value; }";

    /// <summary>A tooltip header: date and time to the second.</summary>
    public const string TooltipSeconds =
        "function (value, opts) { return window.noTime ? window.noTime.tooltipLabel(window.noTime.instantOf(value, opts && opts.w), true) : value; }";

    /// <summary>A tooltip header: the date alone, for a bar that stands for a whole day.</summary>
    public const string TooltipDay =
        "function (value, opts) { return window.noTime ? window.noTime.dateLabel(window.noTime.instantOf(value, opts && opts.w)) : value; }";

    /// <summary>A tooltip header: the time alone.</summary>
    public const string TooltipTime =
        "function (value, opts) { return window.noTime ? window.noTime.tooltipLabel(window.noTime.instantOf(value, opts && opts.w), false, false) : value; }";
}
