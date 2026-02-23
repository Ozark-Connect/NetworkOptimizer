namespace NetworkOptimizer.Core;

/// <summary>
/// Simple static feature flags for toggling features at compile/deploy time.
/// </summary>
public static class FeatureFlags
{
    /// <summary>
    /// When true, the ScheduleService runs and the Schedule tab is visible on /alerts.
    /// Set to false to ship the alerts system without scheduling.
    /// </summary>
    public static bool SchedulingEnabled { get; set; } = true;
}
