namespace NetworkOptimizer.Web.Services;

/// <summary>
/// ApexCharts formatter functions, as the JavaScript strings Blazor-ApexCharts takes, that draw a
/// C#-built chart's times on the viewer's clock (window.noTime, display-time.js). On a viewer whose
/// zone matches the browser's they print what ApexCharts' own formats would.
/// </summary>
public static class ChartTimeFormat
{
    /// <summary>X-axis ticks as datetimeFormatter { hour: "HH:mm", day: "MMM dd" }.</summary>
    public const string Axis =
        "function (value, timestamp, opts) { return window.noTime ? window.noTime.axisLabel(timestamp ?? value, opts) : value; }";

    /// <summary>X-axis ticks as ApexCharts' default datetimeFormatter, whose day reads "dd MMM".</summary>
    public const string AxisDayFirst =
        "function (value, timestamp, opts) { return window.noTime ? window.noTime.axisLabel(timestamp ?? value, opts, 'dd MMM') : value; }";

    /// <summary>A tooltip header as "MMM dd, HH:mm".</summary>
    public const string TooltipMinutes =
        "function (value) { return window.noTime ? window.noTime.tooltipLabel(value, false) : value; }";

    /// <summary>A tooltip header as "MMM dd, HH:mm:ss".</summary>
    public const string TooltipSeconds =
        "function (value) { return window.noTime ? window.noTime.tooltipLabel(value, true) : value; }";

    /// <summary>A tooltip header as "HH:mm".</summary>
    public const string TooltipTime =
        "function (value) { return window.noTime ? window.noTime.tooltipLabel(value, false, false) : value; }";
}
