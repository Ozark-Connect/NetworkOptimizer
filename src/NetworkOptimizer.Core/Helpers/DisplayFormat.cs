namespace NetworkOptimizer.Core.Helpers;

/// <summary>12-hour or 24-hour time.</summary>
public enum ClockStyle
{
    TwelveHour,
    TwentyFourHour,
}

/// <summary>Which comes first in a date: the month, the day, or the year (ISO 8601, numeric).</summary>
public enum DateOrder
{
    MonthFirst,
    DayFirst,
    YearFirst,
}

/// <summary>
/// How a viewer reads dates and times, as .NET format strings. Months are spelled, or numeric only
/// year first (ISO 8601), so a month-first and a day-first reader can never take one date for another.
/// </summary>
/// <param name="Clock">12-hour or 24-hour time.</param>
/// <param name="Order">Month first ("Nov 3"), day first ("3 Nov"), or year first ("2026-11-03").</param>
public sealed record DisplayFormat(ClockStyle Clock, DateOrder Order)
{
    /// <summary>
    /// 24-hour, month first: text with no viewer (alerts, stored reports, plan titles) on an install
    /// with no default format, and a viewer whose browser has not said.
    /// </summary>
    public static readonly DisplayFormat Fallback = new(ClockStyle.TwentyFourHour, DateOrder.MonthFirst);

    /// <summary>
    /// The culture every format here renders with. The server's own culture can drop the AM/PM
    /// designator ("tt" is empty in German) and localize month names the charts print in English.
    /// </summary>
    public static readonly System.Globalization.CultureInfo Culture = System.Globalization.CultureInfo.InvariantCulture;

    private bool H24 => Clock == ClockStyle.TwentyFourHour;
    private bool DayFirst => Order == DateOrder.DayFirst;
    private bool Iso => Order == DateOrder.YearFirst;

    // ISO dates join their time with a space ("2026-11-03 14:05"); spelled ones with a comma.
    private string Join => Iso ? " " : ", ";

    /// <summary>"14:05" or "2:05 PM".</summary>
    public string Time => H24 ? "HH:mm" : "h:mm tt";

    /// <summary>"14:05:09" or "2:05:09 PM".</summary>
    public string TimeSeconds => H24 ? "HH:mm:ss" : "h:mm:ss tt";

    /// <summary>"14:00" or "2 PM", for an hour of the day.</summary>
    public string Hour => H24 ? "HH:00" : "h tt";

    /// <summary>"Nov 3", "3 Nov", or "11-03".</summary>
    public string Day => Iso ? "MM-dd" : DayFirst ? "d MMM" : "MMM d";

    /// <summary>"Nov 3, 2026", "3 Nov 2026", or "2026-11-03".</summary>
    public string Date => Iso ? "yyyy-MM-dd" : DayFirst ? "d MMM yyyy" : "MMM d, yyyy";

    /// <summary>"November 3, 2026", "3 November 2026", or "2026-11-03".</summary>
    public string LongDate => Iso ? "yyyy-MM-dd" : DayFirst ? "d MMMM yyyy" : "MMMM d, yyyy";

    /// <summary>"Nov 3, 14:05", "3 Nov, 14:05", or "11-03 14:05".</summary>
    public string DayTime => $"{Day}{Join}{Time}";

    /// <summary>"Nov 3, 14:05:09", "3 Nov, 14:05:09", or "11-03 14:05:09".</summary>
    public string DayTimeSeconds => $"{Day}{Join}{TimeSeconds}";

    /// <summary>"Nov 3, 2026, 14:05", "3 Nov 2026, 14:05", or "2026-11-03 14:05".</summary>
    public string DateTime => $"{Date}{Join}{Time}";

    /// <summary>"Nov 3, 2026, 14:05:09", "3 Nov 2026, 14:05:09", or "2026-11-03 14:05:09".</summary>
    public string DateTimeSeconds => $"{Date}{Join}{TimeSeconds}";

    /// <summary>"November 3, 2026, 14:05", "3 November 2026, 14:05", or "2026-11-03 14:05".</summary>
    public string LongDateTime => $"{LongDate}{Join}{Time}";

    /// <summary>"Tue Nov 3, 14:05", "Tue 3 Nov, 14:05", or "Tue 11-03 14:05".</summary>
    public string WeekdayDayTime => $"ddd {Day}{Join}{Time}";

    /// <summary>The cookie and preference value: "24-mdy", "12-dmy", "24-ymd".</summary>
    public string Code => $"{(H24 ? "24" : "12")}-{(Iso ? "ymd" : DayFirst ? "dmy" : "mdy")}";

    /// <summary>
    /// Each half (time format, date order) from the first layer that sets it, else from
    /// <paramref name="fallback"/>: a user's own choice over the install default, for instance.
    /// </summary>
    /// <param name="fallback">What stands where no layer sets a half.</param>
    /// <param name="layers">Stored values, first wins: "12"/"24" and "mdy"/"dmy"/"ymd", each null for unset.</param>
    public static DisplayFormat Layered(DisplayFormat fallback, params (string? Clock, string? Order)[] layers)
    {
        ClockStyle? clock = null;
        DateOrder? order = null;
        foreach (var (c, o) in layers)
        {
            clock ??= ParseCode(c).Clock;
            order ??= ParseCode(o).Order;
        }
        return new DisplayFormat(clock ?? fallback.Clock, order ?? fallback.Order);
    }

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
                case "ymd": order = DateOrder.YearFirst; break;
            }
        }
        return (clock, order);
    }
}
