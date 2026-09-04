namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>Mean CPU and memory over one window, and how many samples backed it.</summary>
public sealed record RolloutResourceStats
{
    /// <summary>Mean CPU percentage over the window; null when nothing reported it.</summary>
    public double? CpuPercent { get; init; }

    /// <summary>Mean memory-used percentage over the window; null when nothing reported it.</summary>
    public double? MemoryUsedPercent { get; init; }

    /// <summary>Health samples the means were taken over. Zero means the device was not observed.</summary>
    public int SampleCount { get; init; }

    /// <summary>
    /// Median probe loss over the window when the device is itself a monitored latency target;
    /// null when it is not probed, or was not probed over this window.
    /// </summary>
    public double? LossPercent { get; init; }

    /// <summary>True when the window carried at least one usable reading.</summary>
    public bool HasSamples => SampleCount > 0;
}

/// <summary>Which way a before/after resource comparison came out.</summary>
public enum ResourceComparisonVerdict
{
    /// <summary>Not enough data on one side to say anything.</summary>
    Inconclusive,
    /// <summary>Inside the floors: nothing worth telling anyone about.</summary>
    Unchanged,
    /// <summary>Appreciably heavier after the upgrade.</summary>
    Regression,
    /// <summary>Appreciably lighter after the upgrade.</summary>
    Improvement
}

/// <summary>A before/after comparison and the sentence that explains it.</summary>
/// <param name="Verdict">Which way it came out.</param>
/// <param name="Detail">What moved, with both numbers, for the alert body.</param>
public sealed record ResourceComparison(ResourceComparisonVerdict Verdict, string Detail);

/// <summary>
/// The bars a post-upgrade device has to clear. Every one is a pair - a relative move AND an
/// absolute floor - because either alone is wrong at one end of the range: 2% CPU becoming 4% is a
/// doubling nobody needs told about, and 60% becoming 66% is six points of nothing.
/// </summary>
public static class LitmusThresholds
{
    /// <summary>Relative CPU move that counts, once the absolute floor is also cleared.</summary>
    public const double CpuRelativeFraction = 0.25;

    /// <summary>Absolute CPU points that must move as well as the relative fraction.</summary>
    public const double CpuAbsolutePoints = 10.0;

    /// <summary>Relative memory move that counts, once the absolute floor is also cleared.</summary>
    public const double MemoryRelativeFraction = 0.10;

    /// <summary>
    /// Absolute memory points that must move as well as the relative fraction. Memory sits high and
    /// steady on most devices, so the floor is lower than CPU's - but it is here for the same reason:
    /// a device idling at 3% must not report a regression for drifting to 4%.
    /// </summary>
    public const double MemoryAbsolutePoints = 5.0;

    /// <summary>Relative loss increase that counts, once the absolute floor is also cleared.</summary>
    public const double LossRelativeFraction = 0.25;

    /// <summary>
    /// Median loss over the litmus window that fails a device that is a monitored latency target.
    /// A floor, not a verdict: a target already losing this much before the upgrade has to have got
    /// appreciably worse to fail, or every rollout past a flaky target would abort that whole model.
    /// </summary>
    public const double LossFailPercent = 5.0;

    /// <summary>
    /// Whether post-upgrade loss is bad enough to fail the canary, given what the device was losing
    /// beforehand. Absolute floor first, then - when there was a baseline to beat - a relative rise
    /// on top of it, the same pairing CPU and memory use and for the same reason.
    /// </summary>
    /// <param name="beforeLossPercent">Median loss before the upgrade; null when there was no baseline.</param>
    /// <param name="afterLossPercent">Median loss over the litmus window; null when the device is not probed.</param>
    public static bool IsAppreciableLoss(double? beforeLossPercent, double? afterLossPercent)
    {
        if (afterLossPercent is not double after || after < LossFailPercent) return false;
        if (beforeLossPercent is not double before || before <= 0) return true;
        return (after - before) / before >= LossRelativeFraction;
    }

    /// <summary>
    /// Compares two windows. Both sides need samples, and a metric only votes when both windows
    /// carried it. CPU is checked first: it is the metric an upgrade regression actually shows up in.
    /// </summary>
    /// <param name="before">Pre-upgrade window.</param>
    /// <param name="after">Post-upgrade window.</param>
    public static ResourceComparison Compare(RolloutResourceStats? before, RolloutResourceStats? after)
    {
        if (before is not { HasSamples: true } || after is not { HasSamples: true })
            return new ResourceComparison(ResourceComparisonVerdict.Inconclusive, "Not enough health history to compare.");

        var cpu = CompareMetric(before.CpuPercent, after.CpuPercent, CpuRelativeFraction, CpuAbsolutePoints, "CPU");
        if (cpu.Verdict != ResourceComparisonVerdict.Unchanged && cpu.Verdict != ResourceComparisonVerdict.Inconclusive)
            return cpu;

        var memory = CompareMetric(before.MemoryUsedPercent, after.MemoryUsedPercent, MemoryRelativeFraction, MemoryAbsolutePoints, "Memory");
        if (memory.Verdict != ResourceComparisonVerdict.Unchanged && memory.Verdict != ResourceComparisonVerdict.Inconclusive)
            return memory;

        if (cpu.Verdict == ResourceComparisonVerdict.Inconclusive && memory.Verdict == ResourceComparisonVerdict.Inconclusive)
            return new ResourceComparison(ResourceComparisonVerdict.Inconclusive, "Neither CPU nor memory was reported on both sides.");

        return new ResourceComparison(ResourceComparisonVerdict.Unchanged, "CPU and memory are where they were.");
    }

    /// <summary>
    /// Whether a short-litmus reading is far enough off the pre-upgrade mean to fail the canary.
    /// Same floors as the comparison, increase side only - a device that got lighter is not a fault.
    /// </summary>
    /// <param name="before">Pre-upgrade window.</param>
    /// <param name="after">Post-cool-down window.</param>
    public static bool IsAppreciableIncrease(RolloutResourceStats? before, RolloutResourceStats? after) =>
        Compare(before, after).Verdict == ResourceComparisonVerdict.Regression;

    private static ResourceComparison CompareMetric(
        double? before, double? after, double relativeFraction, double absolutePoints, string label)
    {
        if (before is not double b || after is not double a)
            return new ResourceComparison(ResourceComparisonVerdict.Inconclusive, $"{label} was not reported on both sides.");

        var delta = a - b;
        var absolute = Math.Abs(delta);
        if (absolute < absolutePoints)
            return new ResourceComparison(ResourceComparisonVerdict.Unchanged, $"{label} moved {delta:+0.0;-0.0;0.0} points.");

        // A baseline at or below zero has no meaningful relative move, so the absolute floor decides.
        var relativeCleared = b <= 0 || absolute / b >= relativeFraction;
        if (!relativeCleared)
            return new ResourceComparison(ResourceComparisonVerdict.Unchanged, $"{label} moved {delta:+0.0;-0.0;0.0} points.");

        var verdict = delta > 0 ? ResourceComparisonVerdict.Regression : ResourceComparisonVerdict.Improvement;
        return new ResourceComparison(verdict, $"{label} went from {b:0.0}% to {a:0.0}%.");
    }
}
