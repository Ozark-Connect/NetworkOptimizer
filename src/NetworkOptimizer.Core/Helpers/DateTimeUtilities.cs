namespace NetworkOptimizer.Core.Helpers;

/// <summary>Timestamp handling shared across the app.</summary>
public static class DateTimeUtilities
{
    /// <summary>
    /// A stored timestamp as a genuine UTC instant, safe to hand to InfluxDB.
    ///
    /// Every timestamp this app stores is UTC, but SQLite returns them Unspecified - and
    /// <c>ToUniversalTime</c> reads Unspecified as LOCAL and converts, so on a container running a
    /// non-UTC timezone a window silently shifts by the offset. Twice now that has meant querying
    /// hours that had not happened yet and getting an empty result rather than an error: once for a
    /// firmware rollout's post-upgrade window, once for a speed test's wireless rates.
    ///
    /// Unspecified is taken at face value; anything already Local is converted properly.
    /// </summary>
    public static DateTime AsUtc(DateTime t) =>
        t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime();

    /// <summary>A stored timestamp on <paramref name="zone"/>'s clock. Unspecified is read as UTC, as in <see cref="AsUtc"/>.</summary>
    public static DateTime ToZone(DateTime t, TimeZoneInfo zone) => TimeZoneInfo.ConvertTimeFromUtc(AsUtc(t), zone);

    /// <summary>
    /// A zone's label at an instant: its abbreviation where that is unambiguous ("CDT", "AEST"),
    /// else its offset ("UTC+01:00").
    ///
    /// The platform has no abbreviations, only long names, and their initials are often wrong:
    /// Windows names London "GMT Standard Time" ("GST") and Berlin "W. Europe Standard Time"
    /// ("WEST"), and ICU's "Central European Standard Time" reads "CEST" in winter. So initials are
    /// used only when they are in <see cref="TrustedAbbreviations"/> with the offset in force.
    /// </summary>
    public static string ZoneAbbreviation(TimeZoneInfo zone, DateTime instant)
    {
        if (zone.BaseUtcOffset == TimeSpan.Zero && !zone.SupportsDaylightSavingTime) return "UTC";
        var at = new DateTimeOffset(AsUtc(instant));
        var name = zone.IsDaylightSavingTime(at) ? zone.DaylightName : zone.StandardName;
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var initials = words.Length > 1 ? string.Concat(words.Select(w => w[0])) : name;
        var offset = zone.GetUtcOffset(at);
        return TrustedAbbreviations.TryGetValue(initials, out var expected) && expected == offset.TotalMinutes
            ? initials
            : UtcOffsetLabel(offset);
    }

    /// <summary>"UTC-05:00" for the offset <paramref name="zone"/> has in force at <paramref name="instant"/>.</summary>
    public static string UtcOffsetLabel(TimeZoneInfo zone, DateTime instant) =>
        UtcOffsetLabel(zone.GetUtcOffset(new DateTimeOffset(AsUtc(instant))));

    private static string UtcOffsetLabel(TimeSpan offset) =>
        $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm}";

    /// <summary>
    /// Abbreviations whose meaning is settled, with the UTC offset in minutes each one stands for.
    /// The offset check rejects a collision: "CST" from China Standard Time, "IST" from Israel's.
    /// </summary>
    private static readonly Dictionary<string, double> TrustedAbbreviations = new(StringComparer.Ordinal)
    {
        ["EST"] = -300, ["EDT"] = -240, ["CST"] = -360, ["CDT"] = -300,
        ["MST"] = -420, ["MDT"] = -360, ["PST"] = -480, ["PDT"] = -420,
        ["AKST"] = -540, ["AKDT"] = -480, ["HST"] = -600,
        ["AST"] = -240, ["ADT"] = -180, ["NST"] = -210, ["NDT"] = -150,
        ["GMT"] = 0, ["BST"] = 60, ["IST"] = 330, ["JST"] = 540, ["KST"] = 540,
        ["AWST"] = 480, ["ACST"] = 570, ["ACDT"] = 630, ["AEST"] = 600, ["AEDT"] = 660,
        ["NZST"] = 720, ["NZDT"] = 780,
    };
}
