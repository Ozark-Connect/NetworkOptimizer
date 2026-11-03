namespace NetworkOptimizer.Web.Services;

/// <summary>12-hour or 24-hour time.</summary>
public enum ClockStyle
{
    TwelveHour,
    TwentyFourHour,
}

/// <summary>Which comes first in a date: the month or the day.</summary>
public enum DateOrder
{
    MonthFirst,
    DayFirst,
}

/// <summary>
/// How a viewer reads dates and times, as .NET format strings. Months are always spelled, so a
/// month-first and a day-first reader can never take one date for another.
/// </summary>
/// <param name="Clock">12-hour or 24-hour time.</param>
/// <param name="Order">Month first ("Nov 3") or day first ("3 Nov").</param>
public sealed record DisplayFormat(ClockStyle Clock, DateOrder Order)
{
    /// <summary>
    /// For text with no viewer (alerts, stored reports, plan titles), and a viewer whose browser
    /// has not said: 24-hour, month first.
    /// </summary>
    public static readonly DisplayFormat Fallback = new(ClockStyle.TwentyFourHour, DateOrder.MonthFirst);

    /// <summary>
    /// The culture every format here renders with. The server's own culture can drop the AM/PM
    /// designator ("tt" is empty in German) and localize month names the charts print in English.
    /// </summary>
    public static readonly System.Globalization.CultureInfo Culture = System.Globalization.CultureInfo.InvariantCulture;

    private bool H24 => Clock == ClockStyle.TwentyFourHour;
    private bool DayFirst => Order == DateOrder.DayFirst;

    /// <summary>"14:05" or "2:05 PM".</summary>
    public string Time => H24 ? "HH:mm" : "h:mm tt";

    /// <summary>"14:05:09" or "2:05:09 PM".</summary>
    public string TimeSeconds => H24 ? "HH:mm:ss" : "h:mm:ss tt";

    /// <summary>"14:00" or "2 PM", for an hour of the day.</summary>
    public string Hour => H24 ? "HH:00" : "h tt";

    /// <summary>"Nov 3" or "3 Nov".</summary>
    public string Day => DayFirst ? "d MMM" : "MMM d";

    /// <summary>"Nov 3, 2026" or "3 Nov 2026".</summary>
    public string Date => DayFirst ? "d MMM yyyy" : "MMM d, yyyy";

    /// <summary>"November 3, 2026" or "3 November 2026".</summary>
    public string LongDate => DayFirst ? "d MMMM yyyy" : "MMMM d, yyyy";

    /// <summary>"Nov 3, 14:05" or "3 Nov, 14:05".</summary>
    public string DayTime => $"{Day}, {Time}";

    /// <summary>"Nov 3, 14:05:09" or "3 Nov, 14:05:09".</summary>
    public string DayTimeSeconds => $"{Day}, {TimeSeconds}";

    /// <summary>"Nov 3, 2026, 14:05" or "3 Nov 2026, 14:05".</summary>
    public string DateTime => $"{Date}, {Time}";

    /// <summary>"Nov 3, 2026, 14:05:09" or "3 Nov 2026, 14:05:09".</summary>
    public string DateTimeSeconds => $"{Date}, {TimeSeconds}";

    /// <summary>"Tue Nov 3, 14:05" or "Tue 3 Nov, 14:05".</summary>
    public string WeekdayDayTime => $"ddd {Day}, {Time}";

    /// <summary>The cookie and preference value: "24-mdy", "12-dmy".</summary>
    public string Code => $"{(H24 ? "24" : "12")}-{(DayFirst ? "dmy" : "mdy")}";

    /// <summary>Reads <see cref="Code"/>, or either half of it; null for anything else.</summary>
    public static (ClockStyle? Clock, DateOrder? Order) ParseCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return (null, null);
        ClockStyle? clock = null;
        DateOrder? order = null;
        foreach (var part in code.Trim().Split('-'))
        {
            switch (part)
            {
                case "12": clock = ClockStyle.TwelveHour; break;
                case "24": clock = ClockStyle.TwentyFourHour; break;
                case "mdy": order = DateOrder.MonthFirst; break;
                case "dmy": order = DateOrder.DayFirst; break;
            }
        }
        return (clock, order);
    }
}
