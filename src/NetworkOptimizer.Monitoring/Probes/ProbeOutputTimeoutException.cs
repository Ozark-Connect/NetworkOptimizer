namespace NetworkOptimizer.Monitoring.Probes;

/// <summary>
/// Thrown when a probe child process's redirected output read never completes
/// within the grace period: the child has exited (or been killed) but the
/// pipe-read completion was never delivered (wedged async engine). The probe
/// has NO trustworthy outcome, so callers must drop the sample entirely -
/// parsing the missing output as empty would fabricate a 100% loss result,
/// planting false outages in monitoring data and the alert evaluators.
/// </summary>
public sealed class ProbeOutputTimeoutException : IOException
{
    public ProbeOutputTimeoutException(string message) : base(message)
    {
    }
}
