namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Animates coarse WAN speed-test progress while a run that can't stream live progress is in
/// flight - the gateway SSH run and the agent run (which returns only the final JSON over the
/// tunnel). It steps through the same discovery/latency/download/upload timeline the local run
/// reports for real, and stops the moment the running task completes. The local (this-server)
/// run keeps its accurate per-line stdout progress and does not use this.
/// </summary>
public static class WanSpeedTestProgressAnimator
{
    // Timeline: discovery/latency ~2.5s, then download and upload ramps. Total ~21.5s to
    // roughly match a real run; the loop below stops early whenever the task actually finishes.
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
    /// Reports simulated progress against <paramref name="runningTask"/> until it completes.
    /// Never throws on cancellation; the caller awaits <paramref name="runningTask"/> for the result.
    /// </summary>
    public static async Task AnimateAsync(Task runningTask, Action<string, int, string?> report, CancellationToken ct)
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
}
