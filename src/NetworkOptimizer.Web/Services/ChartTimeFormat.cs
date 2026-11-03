namespace NetworkOptimizer.Web.Services;

/// <summary>
/// ApexCharts formatter functions, as the JavaScript strings Blazor-ApexCharts takes, that draw a
/// C#-built chart's times on the viewer's clock and in their date and time format (window.noTime,
/// display-time.js), the same as the module charts.
/// </summary>
public static class ChartTimeFormat
{
    /// <summary>X-axis ticks: the date at midnight, else the time.</summary>
    public const string Axis =
        "function (value, timestamp, opts) { return window.noTime ? window.noTime.axisLabel(timestamp ?? value, opts) : value; }";

    /// <summary>The same as <see cref="Axis"/>; kept for the chart that once used ApexCharts' default day form.</summary>
    public const string AxisDayFirst = Axis;

    /// <summary>A tooltip header: date and time to the minute.</summary>
    public const string TooltipMinutes =
        "function (value) { return window.noTime ? window.noTime.tooltipLabel(value, false) : value; }";

    /// <summary>A tooltip header: date and time to the second.</summary>
    public const string TooltipSeconds =
        "function (value) { return window.noTime ? window.noTime.tooltipLabel(value, true) : value; }";

    /// <summary>A tooltip header: the date alone, for a bar that stands for a whole day.</summary>
    public const string TooltipDay =
        "function (value) { return window.noTime ? window.noTime.dateLabel(value) : value; }";

    /// <summary>A tooltip header: the time alone.</summary>
    public const string TooltipTime =
        "function (value) { return window.noTime ? window.noTime.tooltipLabel(value, false, false) : value; }";
}
