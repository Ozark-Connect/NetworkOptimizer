namespace NetworkOptimizer.AgentProtocol;

/// <summary>
/// How long the agent waits before redialing its tunnel. A tunnel that carried traffic for
/// <see cref="HealthyRun"/> (server hello to its last message) redials within seconds however it ended (a clean close, a reset, or
/// the agent's own silence watchdog): reverse proxies recycle long-lived streams both ways, and
/// the server shows the site's console as awaiting the agent until the tunnel is back.
///
/// Everything else keeps the full <see cref="Backoff"/>: a failed dial, a missing server hello,
/// a refusal (revoked or license-restricted), and a stream that keeps dying young. A path that
/// cuts the tunnel every minute or two is a misconfigured proxy the server flags as reconnecting
/// often; redialing it at once would only repeat the config and blocklist push faster.
/// </summary>
public static class TunnelReconnectPolicy
{
    /// <summary>The wait after anything but a long, healthy run.</summary>
    public static readonly TimeSpan Backoff = TimeSpan.FromSeconds(30);

    /// <summary>How long a tunnel must have carried traffic to redial at once.</summary>
    public static readonly TimeSpan HealthyRun = TimeSpan.FromMinutes(5);

    /// <summary>Whether a tunnel healthy for <paramref name="healthyFor"/> redials at once. Null: it never opened.</summary>
    public static bool RedialsAtOnce(TimeSpan? healthyFor) => healthyFor >= HealthyRun;

    /// <summary>
    /// The wait before the next dial. The 1-3 s jitter spreads a fleet's redials when one server
    /// restart ends every tunnel in the same second.
    /// </summary>
    public static TimeSpan Delay(TimeSpan? healthyFor, Random random) =>
        RedialsAtOnce(healthyFor) ? TimeSpan.FromMilliseconds(1000 + random.Next(2000)) : Backoff;
}
