namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Drops round-trip figures no probe could have measured, before they are stored or scored.
/// <c>ping</c> computes a reply's time from a timestamp carried in the packet, so a reply with a
/// mangled payload prints a time of years, and the summary's average, maximum, and jitter take it in.
/// </summary>
public static class LatencySanity
{
    /// <summary>The longest round trip accepted, in ms. Far above any probe's per-reply timeout.</summary>
    public const double MaxPlausibleRttMs = 60_000;

    /// <summary>True for an absent value, or a finite one from 0 to <see cref="MaxPlausibleRttMs"/>.</summary>
    public static bool IsPlausible(double? ms) =>
        ms is not double v || (double.IsFinite(v) && v >= 0 && v <= MaxPlausibleRttMs);

    /// <summary>
    /// The burst's figures, unchanged when all are plausible. Otherwise only a plausible minimum
    /// survives: one bad reply is inside the average, the maximum, and the jitter, and the burst
    /// keeps no per-reply times to rebuild them from.
    /// </summary>
    public static (double? Min, double? Avg, double? Max, double? Jitter) Clean(
        double? min, double? avg, double? max, double? jitter) =>
        IsPlausible(min) && IsPlausible(avg) && IsPlausible(max) && IsPlausible(jitter)
            ? (min, avg, max, jitter)
            : (IsPlausible(min) ? min : null, null, null, null);
}
