using ApexCharts;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Time axes and tooltip headers for a C#-built chart drawn on the viewer's wall clock: its x values
/// are <see cref="DisplayTimeExtensions.ToChart"/>, and the strings are the JavaScript functions
/// Blazor-ApexCharts takes, reading window.noTime (display-time.js) the same as the module charts.
/// </summary>
public static class ChartTimeFormat
{
    /// <summary>
    /// Puts ApexCharts' own time scale on a wall-clock axis, in the viewer's format: it places every
    /// tick and day break, and only the text follows the viewer. Never set a label Formatter on a time
    /// axis: any formatter turns that scale off and ApexCharts spaces ticks evenly with no breaks.
    /// </summary>
    /// <param name="labels">The chart's x-axis labels.</param>
    /// <param name="displayTime">The viewer's clock and format.</param>
    public static void ApplyTimeAxis(XAxisLabels labels, IDisplayTimeZone displayTime)
    {
        labels.DatetimeUTC = true;
        labels.DatetimeFormatter = DatetimeFormatterFor(displayTime.Format);
        labels.Formatter = null;
    }

    /// <summary>
    /// ApexCharts' datetimeFormatter tokens for a format; year and month keep ApexCharts' defaults.
    /// The twin of display-time.js datetimeFormatter.
    /// </summary>
    /// <param name="format">The viewer's format.</param>
    public static DatetimeFormatter DatetimeFormatterFor(DisplayFormat format)
    {
        var h24 = format.Clock == ClockStyle.TwentyFourHour;
        var seconds = h24 ? "HH:mm:ss" : "h:mm:ss TT";
        return new DatetimeFormatter
        {
            Year = "yyyy",
            Month = "MMM 'yy",
            Day = format.Order == DateOrder.DayFirst ? "dd MMM" : "MMM dd",
            Hour = h24 ? "HH:mm" : "h:mm TT",
            Minute = seconds,
            Second = seconds,
        };
    }

    /// <summary>A tooltip header: date and time to the minute.</summary>
    public const string TooltipMinutes =
        "function (value) { return window.noTime ? window.noTime.tooltipLabel(window.noTime.fromChart(value), false) : value; }";

    /// <summary>A tooltip header: date and time to the second.</summary>
    public const string TooltipSeconds =
        "function (value) { return window.noTime ? window.noTime.tooltipLabel(window.noTime.fromChart(value), true) : value; }";

    /// <summary>A tooltip header: the date alone, for a bar that stands for a whole day.</summary>
    public const string TooltipDay =
        "function (value) { return window.noTime ? window.noTime.dateLabel(window.noTime.fromChart(value)) : value; }";

    /// <summary>A tooltip header: the time alone.</summary>
    public const string TooltipTime =
        "function (value) { return window.noTime ? window.noTime.tooltipLabel(window.noTime.fromChart(value), false, false) : value; }";
}
