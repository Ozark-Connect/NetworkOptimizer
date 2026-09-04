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
}
