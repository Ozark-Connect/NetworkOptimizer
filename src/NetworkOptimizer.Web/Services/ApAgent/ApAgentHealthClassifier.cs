namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Turns one observation of an AP's agent into a state and the single action it warrants.
///
/// "Not answering" is not one condition, and most of its causes must NOT trigger a redeploy. RST
/// versus timeout is the whole diagnostic: a refusal proves the packet reached the AP and nothing
/// was listening, while a timeout proves only that something in the path swallowed it - and a path
/// that drops port 8899 almost certainly drops SSH too, so a redeploy fails the same way while
/// hiding the real cause.
/// </summary>
public static class ApAgentHealthClassifier
{
    /// <summary>
    /// How far the agent's own probe clock may fall behind its response clock before it counts as
    /// wedged. Three times the agent's 300 s probe interval, so a single slow pass is not a verdict.
    /// </summary>
    public static readonly TimeSpan ProbeStaleAfter = TimeSpan.FromMinutes(15);

    /// <summary>Assesses one observation.</summary>
    public static ApAgentAssessment Classify(ApAgentObservation observation)
        => Classify(observation, ProbeStaleAfter);

    /// <summary>Assesses one observation against a caller-chosen staleness threshold.</summary>
    public static ApAgentAssessment Classify(ApAgentObservation observation, TimeSpan probeStaleAfter)
    {
        // Architecture is a fact about the hardware, so it outranks reachability: an unsupported AP
        // that happens to be answering is still unsupported, and no action makes it supported.
        if (!observation.SupportedArchitecture)
        {
            return new ApAgentAssessment(ApAgentState.Unsupported, ApAgentAction.None,
                observation.Detail ?? "This access point's architecture has no AP Agent build.");
        }

        // An AP that is down is not an agent problem. Checked before the connection outcome so a
        // rebooting AP does not spend its downtime consuming SSH attempts and backoff steps.
        if (!observation.DeviceOnline)
        {
            return new ApAgentAssessment(ApAgentState.ApOffline, ApAgentAction.Wait,
                observation.Detail ?? "The access point is offline; nothing to deploy to yet.");
        }

        switch (observation.Reach)
        {
            case ApAgentReach.Refused:
                return new ApAgentAssessment(ApAgentState.NotListening, ApAgentAction.Redeploy,
                    observation.Detail ?? "The access point refused the connection, so nothing is listening. Redeploying.");

            case ApAgentReach.TimedOut:
                return new ApAgentAssessment(ApAgentState.Filtered, ApAgentAction.SurfacePathProblem,
                    observation.Detail ?? "The connection was dropped rather than refused, which means something in the path is filtering it. SSH is likely blocked the same way, so a redeploy would not help.");

            case ApAgentReach.Unreachable:
                return new ApAgentAssessment(ApAgentState.Filtered, ApAgentAction.SurfacePathProblem,
                    observation.Detail ?? "There is no route to the access point from this server.");

            case ApAgentReach.NotAttempted:
                return new ApAgentAssessment(ApAgentState.Unknown, ApAgentAction.None,
                    observation.Detail ?? "Not checked yet.");

            case ApAgentReach.Unknown:
                return new ApAgentAssessment(ApAgentState.Unhealthy, ApAgentAction.None,
                    observation.Detail ?? "The connection failed without saying why. Waiting for a clearer answer rather than redeploying on a guess.");

            case ApAgentReach.Answered:
                break;
        }

        return ClassifyAnswer(observation, probeStaleAfter);
    }

    private static ApAgentAssessment ClassifyAnswer(ApAgentObservation observation, TimeSpan probeStaleAfter)
    {
        var status = observation.HttpStatus ?? 0;

        if (status == 401 || status == 403)
        {
            return new ApAgentAssessment(ApAgentState.Unauthorized, ApAgentAction.RepushConfig,
                observation.Detail ?? "The agent is running but rejected this server's token. Pushing the token again; the binary is fine.");
        }

        if (status is < 200 or > 299 || observation.Health is null)
        {
            return new ApAgentAssessment(ApAgentState.Unhealthy, ApAgentAction.None,
                observation.Detail ?? $"The agent answered with HTTP {status} and no usable health payload.");
        }

        var health = observation.Health;

        // Staleness is measured entirely inside the agent's own response - its probe clock against
        // its response clock - so a wrong clock on the AP cannot fake a wedge.
        if (health.LastProbeRun != default && health.CollectedAt - health.LastProbeRun > probeStaleAfter)
        {
            var behind = health.CollectedAt - health.LastProbeRun;
            return new ApAgentAssessment(ApAgentState.Wedged, ApAgentAction.RestartInPlace,
                $"The agent is answering but has not run a probe for {(int)behind.TotalMinutes} minutes. Restarting it in place; the binary does not need re-transferring.");
        }

        if (health.BinaryVersion < observation.ExpectedBinaryVersion)
        {
            return new ApAgentAssessment(ApAgentState.OutOfDate, ApAgentAction.Upgrade,
                $"Healthy, running agent contract version {health.BinaryVersion} against this server's {observation.ExpectedBinaryVersion}.");
        }

        var detail = health.Degraded && health.Unavailable.Count > 0
            ? $"Healthy. Degraded probes: {string.Join(", ", health.Unavailable)}."
            : "Healthy.";

        return new ApAgentAssessment(ApAgentState.Healthy, ApAgentAction.None, detail);
    }

    /// <summary>
    /// Maps a tunnel-proxy open failure to a reach outcome. An agent-routed AP is dialed on a
    /// loopback listener, so the local socket says nothing about the far side; the site's agent
    /// reports the real reason and it arrives as free text from its own socket layer.
    /// </summary>
    public static ApAgentReach ReachFromTunnelFailure(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return ApAgentReach.Unknown;
        if (reason.Contains("refused", StringComparison.OrdinalIgnoreCase)) return ApAgentReach.Refused;
        if (reason.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("timeout", StringComparison.OrdinalIgnoreCase)) return ApAgentReach.TimedOut;
        if (reason.Contains("unreachable", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("no route", StringComparison.OrdinalIgnoreCase)) return ApAgentReach.Unreachable;
        return ApAgentReach.Unknown;
    }
}
