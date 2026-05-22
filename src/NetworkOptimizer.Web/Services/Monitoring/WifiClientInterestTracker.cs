using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Tracks which WiFi client MACs are actively visible on any 3D map session.
/// The map JS heartbeats its visible clients every few seconds; entries expire
/// after 30 seconds of no heartbeat (tab closed, navigated away). The enhanced
/// WiFi tier polls WiFiMan for the top-N active clients in this set.
/// </summary>
public class WifiClientInterestTracker
{
    private readonly ConcurrentDictionary<string, DateTime> _interested = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan Expiry = TimeSpan.FromSeconds(30);

    public void Heartbeat(IEnumerable<string> clientMacs)
    {
        var now = DateTime.UtcNow;
        foreach (var mac in clientMacs)
        {
            if (!string.IsNullOrEmpty(mac))
                _interested[mac.ToLowerInvariant().Replace('-', ':')] = now;
        }
        Prune();
    }

    public IReadOnlyList<string> GetActiveClients()
    {
        Prune();
        return _interested.Keys.ToList();
    }

    public bool HasActiveClients => !_interested.IsEmpty;

    private void Prune()
    {
        var cutoff = DateTime.UtcNow - Expiry;
        foreach (var kvp in _interested)
        {
            if (kvp.Value < cutoff)
                _interested.TryRemove(kvp.Key, out _);
        }
    }
}
