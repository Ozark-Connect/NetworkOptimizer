namespace NetworkOptimizer.AgentProtocol;

/// <summary>
/// Builds the uwnspeedtest command-line arguments for a <see cref="UwnRequest"/>.
/// Shared contract logic so the agent (which runs the binary at the site) invokes
/// it exactly the way the central server invokes its own local binary:
/// <c>-streams N -servers N -duration N -timeout N</c>. Keeping the builder in one
/// place means a change to the invocation can't drift between the two runners.
/// </summary>
public static class UwnClientArgs
{
    /// <summary>The uwnspeedtest arguments for the given stream/server/timing parameters.</summary>
    public static string Build(int streams, int servers, int durationSeconds, int timeoutSeconds) =>
        $"-streams {streams} -servers {servers} -duration {durationSeconds} -timeout {timeoutSeconds}";

    /// <summary>The uwnspeedtest arguments for the given request.</summary>
    public static string Build(UwnRequest request) =>
        Build(request.Streams, request.Servers, request.DurationSeconds, request.TimeoutSeconds);
}
