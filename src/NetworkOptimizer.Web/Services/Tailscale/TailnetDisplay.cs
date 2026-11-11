using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.Tailscale;

/// <summary>How tailnet details read on screen, shared by every page that shows them.</summary>
public static class TailnetDisplay
{
    /// <summary>
    /// "Direct", "Relayed (DERP Dallas)", or "Peer relay". <paramref name="afterTest"/> marks a path the
    /// gateway saw after a speed test rather than during it.
    /// </summary>
    public static string PathLabel(TailnetPathInfo path, bool afterTest = false)
    {
        var label = path.Kind switch
        {
            TailnetPathKind.Direct => "Direct",
            TailnetPathKind.Derp => (path.RelayName ?? path.Relay) is { Length: > 0 } region ? $"Relayed (DERP {region})" : "Relayed (DERP)",
            _ => "Peer relay",
        };
        return afterTest && path.Observed ? $"{label} (seen just after the test)" : label;
    }

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

    /// <summary>The device's owner, or for a tagged device (no owner) its tags; null when neither.</summary>
    public static string? Owner(TailnetPeer peer) =>
        !string.IsNullOrWhiteSpace(peer.User) ? peer.User
        : peer.Tags.Count > 0 ? string.Join(", ", peer.Tags)
        : null;

    /// <summary>An owner login shortened for one line ("user1@exam…"); the full value goes in a tooltip.</summary>
    public static string ShortOwner(string owner, int max = 24) =>
        owner.Length <= max ? owner : owner[..(max - 1)] + "…";
}
