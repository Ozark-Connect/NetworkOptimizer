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
        if (PathEndpoint(path) is { } endpoint) parts.Add(endpoint);
        else if (path.Kind == TailnetPathKind.Derp) parts.Add("No direct UDP path: traffic goes through Tailscale's relay");
        if (PathHandshake(path) is { } handshake) parts.Add($"handshake {handshake}");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>The direct endpoint or the peer relay's address; null on a DERP path.</summary>
    public static string? PathEndpoint(TailnetPathInfo path) =>
        path.Kind is TailnetPathKind.Direct or TailnetPathKind.PeerRelay && !string.IsNullOrEmpty(path.Endpoint) ? path.Endpoint : null;

    /// <summary>"48 seconds before the read". Relative to the read, not to now: a speed test result keeps the path it had at test time.</summary>
    public static string? PathHandshake(TailnetPathInfo path) =>
        path.LastHandshake is { } at && path.ReadAt >= at
            ? $"{TimeFormatHelper.FormatDurationExact(path.ReadAt - at)} before the read"
            : null;

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
