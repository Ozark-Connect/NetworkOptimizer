namespace NetworkOptimizer.UniFi.Models;

/// <summary>How tailscaled carried traffic to one peer, as its local status reported it.</summary>
public sealed class TailnetPathInfo
{
    /// <summary>Direct, a DERP relay, or a peer relay.</summary>
    public TailnetPathKind Kind { get; set; }

    /// <summary>The DERP region code ("dfw"). For a direct path it is the peer's home region, not a relay.</summary>
    public string? Relay { get; set; }

    /// <summary>The peer's endpoint (ip:port) on a direct path, or the relay's on a peer relay.</summary>
    public string? Endpoint { get; set; }

    /// <summary>The last WireGuard handshake with the peer, UTC; null when none was reported.</summary>
    public DateTime? LastHandshake { get; set; }

    /// <summary>When the status was read, UTC.</summary>
    public DateTime ReadAt { get; set; }
}

/// <summary>The kind of path tailscaled used to a peer.</summary>
public enum TailnetPathKind
{
    /// <summary>A direct UDP path between the two nodes.</summary>
    Direct,

    /// <summary>Relayed through one of Tailscale's DERP servers.</summary>
    Derp,

    /// <summary>Relayed through another tailnet node acting as a peer relay.</summary>
    PeerRelay,
}
