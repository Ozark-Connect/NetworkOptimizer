namespace NetworkOptimizer.AgentProtocol;

/// <summary>
/// Shared names for reading the tailscaled on an agent's host. The server asks through a
/// <see cref="ProbeRequest"/> with one of these kinds, sent only to an agent advertising
/// <see cref="Capability"/>; an older agent would run a ping instead.
/// </summary>
public static class TailscaleLocal
{
    /// <summary>The capability string, shared by agent and server.</summary>
    public const string Capability = "tailscale-local";

    /// <summary>Probe kind: the host tailscaled's parsed status. The request's address is the socket path, or empty.</summary>
    public const string StatusKind = "tailscale-status";

    /// <summary>Probe kind: the host's source NAT for tailnet peers, from its conntrack table.</summary>
    public const string SnatKind = "tailscale-snat";
}
