using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.Tailscale;

/// <summary>How tailnet details read on screen, shared by every page that shows them.</summary>
public static class TailnetDisplay
{
    /// <summary>"Direct", "Relayed (DERP dfw)", or "Peer relay".</summary>
    public static string PathLabel(TailnetPathInfo path) => path.Kind switch
    {
        TailnetPathKind.Direct => "Direct",
        TailnetPathKind.Derp => string.IsNullOrEmpty(path.Relay) ? "Relayed (DERP)" : $"Relayed (DERP {path.Relay})",
        _ => "Peer relay",
    };

    /// <summary>The endpoint and the last handshake, for the label's tooltip; null when there is neither.</summary>
    public static string? PathDetail(TailnetPathInfo path)
    {
        var parts = new List<string>();
        if (path.Kind == TailnetPathKind.Direct && !string.IsNullOrEmpty(path.Endpoint))
            parts.Add($"Direct to {path.Endpoint}");
        else if (path.Kind == TailnetPathKind.PeerRelay && !string.IsNullOrEmpty(path.Endpoint))
            parts.Add($"Through the peer relay at {path.Endpoint}");
        else if (path.Kind == TailnetPathKind.Derp)
            parts.Add("No direct UDP path: traffic goes through Tailscale's relay");
        // Relative to the read, not to now: a speed test result keeps the path it had at test time.
        if (path.LastHandshake is { } at && path.ReadAt >= at)
            parts.Add($"last handshake {TimeFormatHelper.FormatDurationExact(path.ReadAt - at)} before this was read");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>"Received 1.2 GB, sent 34 MB" from this node's counters; null when they are not known.</summary>
    public static string? Traffic(TailnetPeer peer) =>
        peer.RxBytes is { } rx && peer.TxBytes is { } tx && rx + tx > 0
            ? $"Received {DisplayFormatters.FormatBytes(rx)}, sent {DisplayFormatters.FormatBytes(tx)}"
            : null;
}
