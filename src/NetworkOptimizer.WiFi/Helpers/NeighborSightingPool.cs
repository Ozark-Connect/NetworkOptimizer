using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Helpers;

/// <summary>
/// Stabilizes scan-derived neighbor lists by unioning recent sightings. A single UniFi RF scan
/// under-detects neighbors that aren't transmitting during its window, so an unchanged channel can
/// read busy one scan and clear the next. That makes marginal channel-plan moves flicker in and
/// out between runs. Pooling keeps a neighbor in the picture - at its strongest recent signal -
/// until it has been absent for the whole retention window, so a channel only reads clean when it
/// is consistently clean, not just in one sparse scan.
/// </summary>
public static class NeighborSightingPool
{
    /// <summary>
    /// Union the sightings by BSSID, keeping the strongest-signal sighting of each that falls within
    /// <paramref name="window"/> of <paramref name="now"/>. Sightings outside the window, or without
    /// a BSSID, are dropped. The bias is intentionally conservative (strongest, not latest): a
    /// channel won't be treated as clean just because the most recent scan happened to miss a
    /// neighbor it saw moments ago.
    /// </summary>
    public static List<NeighborNetwork> Union(
        IEnumerable<(NeighborNetwork Neighbor, DateTimeOffset SeenAt)> sightings,
        DateTimeOffset now,
        TimeSpan window)
    {
        return sightings
            .Where(s => now - s.SeenAt <= window && !string.IsNullOrEmpty(s.Neighbor.Bssid))
            .GroupBy(s => s.Neighbor.Bssid, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(s => s.Neighbor.Signal ?? int.MinValue).First().Neighbor)
            .ToList();
    }
}
