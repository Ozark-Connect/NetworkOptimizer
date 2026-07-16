namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Starlink terminal health anchors used by ISP Health's Physical Link factor.
/// Sourced from the dish's own reporting conventions: the Starlink app flags
/// obstruction above roughly 1-2% of sky time, and a healthy stationary dish
/// with clear sky sits well under 0.1% obstructed with near-zero dish-side
/// ping drop. Starting anchors, meant to be nudged from field feedback.
/// </summary>
public static class StarlinkHealthThresholds
{
    /// <summary>Fraction of sky time obstructed that still reads as excellent (0.1%).</summary>
    public const double ObstructionFractionGood = 0.001;

    /// <summary>Obstruction fraction where service impact becomes noticeable (2%); the app-level "obstructed" territory.</summary>
    public const double ObstructionFractionPoor = 0.02;

    /// <summary>Obstruction fraction treated as fully degraded (10%).</summary>
    public const double ObstructionFractionCritical = 0.10;

    /// <summary>Mean dish-to-ground ping drop rate that still reads as excellent (0.2%).</summary>
    public const double DropRateGood = 0.002;

    /// <summary>Mean drop rate where the link is clearly degraded (5%).</summary>
    public const double DropRatePoor = 0.05;

    /// <summary>Outage seconds per day that still reads as excellent (a single brief beam re-range).</summary>
    public const double OutageSecondsPerDayGood = 10;

    /// <summary>Outage seconds per day that is clearly degraded (~5 min/day of dead air).</summary>
    public const double OutageSecondsPerDayPoor = 300;
}
