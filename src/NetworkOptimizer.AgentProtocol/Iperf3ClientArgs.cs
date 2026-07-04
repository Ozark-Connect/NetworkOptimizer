namespace NetworkOptimizer.AgentProtocol;

/// <summary>
/// Builds the iperf3 client command-line arguments for an <see cref="Iperf3ClientRequest"/>.
/// Shared contract logic so the agent (which runs the client at the site) produces
/// exactly the same invocation the central server uses for its own local runs:
/// <c>-c host -p port -t duration -P streams -J --connect-timeout 5000 [-R]</c>.
/// </summary>
public static class Iperf3ClientArgs
{
    /// <summary>Connection timeout in milliseconds - fail fast when no server is listening.</summary>
    public const int ConnectTimeoutMs = 5000;

    /// <summary>
    /// The iperf3 client arguments for the given request. Reverse mode adds
    /// <c>-R</c> (server sends to client, the "To Device" direction).
    /// </summary>
    public static string Build(Iperf3ClientRequest request)
    {
        var args = $"-c {request.Host} -p {request.Port} -t {request.DurationSeconds} " +
                   $"-P {request.ParallelStreams} -J --connect-timeout {ConnectTimeoutMs}";
        if (request.Reverse)
            args += " -R";
        return args;
    }
}
