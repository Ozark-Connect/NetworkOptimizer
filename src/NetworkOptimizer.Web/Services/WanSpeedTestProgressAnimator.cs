namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Simulated WAN speed-test progress for runs that can't stream live progress - the gateway SSH
/// run and the agent run (which returns only the final JSON over the tunnel). Two shapes, chosen
/// by how the consuming page renders the value:
/// <list type="bullet">
///   <item><see cref="AnimateStepsAsync"/> - fine-grained percentages shown directly (the gateway
///   page renders the reported percent as-is).</item>
///   <item><see cref="AnimatePhasesAsync"/> - phase boundaries only, which the WAN page interpolates
///   across the download and upload phases (the agent run). Fine-grained steps would fight that
///   page-side interpolation and make the bar jump.</item>
/// </list>
/// Both stop the moment the running task completes. The local (this-server) run keeps its accurate
/// per-line stdout progress and uses neither.
/// </summary>
public static class WanSpeedTestProgressAnimator
{
    // Fine-grained timeline for the gateway run (shown directly). Total ~21.5s to roughly match a
    // real run; the loop stops early whenever the task actually finishes.
    private static readonly (string Phase, int Percent, string Status, int DelayMs)[] Steps =
    {
        ("Discovering servers", 10, "Discovering servers...", 1500),
        ("Testing latency", 15, "Measuring latency...", 1000),
        ("Testing download", 22, "Testing download...", 1800),
        ("Testing download", 30, "Testing download...", 1800),
        ("Testing download", 38, "Testing download...", 1800),
        ("Testing download", 44, "Testing download...", 1800),
        ("Testing download", 50, "Testing download...", 1800),
        ("Testing upload", 58, "Testing upload...", 1800),
        ("Testing upload", 66, "Testing upload...", 1800),
        ("Testing upload", 74, "Testing upload...", 1800),
        ("Testing upload", 82, "Testing upload...", 1800),
        ("Testing upload", 90, "Testing upload...", 1800),
    };

    /// <summary>
    /// Fine-grained steps for a run whose page shows the reported percent directly (gateway).
    /// Never throws on cancellation; the caller awaits <paramref name="runningTask"/> for the result.
    /// </summary>
    public static async Task AnimateStepsAsync(Task runningTask, Action<string, int, string?> report, CancellationToken ct)
    {
        foreach (var step in Steps)
        {
            if (runningTask.IsCompleted) break;
            try { await Task.WhenAny(runningTask, Task.Delay(step.DelayMs, ct)); }
            catch (OperationCanceledException) { break; }
            if (!runningTask.IsCompleted)
                report(step.Phase, step.Percent, step.Status);
        }
    }

    /// <summary>
    /// Phase-boundary progress for a run whose page interpolates download (20-&gt;55) and upload
    /// (60-&gt;95) between the reported phases (agent). Holds each of download/upload for the test
    /// duration so the interpolation doesn't run out early. Never throws on cancellation.
    /// </summary>
    public static async Task AnimatePhasesAsync(Task runningTask, Action<string, int, string?> report, int durationSeconds, CancellationToken ct)
    {
        var phaseMs = Math.Max(8000, durationSeconds * 1000);
        var phases = new (string Phase, int Percent, string Status, int DelayMs)[]
        {
            ("Discovering servers", 5, "Discovering servers...", 2500),
            ("Testing latency", 10, "Measuring latency...", 1500),
            ("Testing download", 20, "Testing download...", phaseMs),
            ("Testing upload", 60, "Testing upload...", phaseMs),
        };

        foreach (var phase in phases)
        {
            if (runningTask.IsCompleted) break;
            report(phase.Phase, phase.Percent, phase.Status);
            try { await Task.WhenAny(runningTask, Task.Delay(phase.DelayMs, ct)); }
            catch (OperationCanceledException) { break; }
        }
    }
}
