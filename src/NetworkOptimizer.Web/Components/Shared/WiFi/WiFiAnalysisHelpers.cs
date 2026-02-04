using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.Web.Components.Shared.WiFi;

/// <summary>
/// Shared helper methods for WiFi analysis components.
/// </summary>
public static class WiFiAnalysisHelpers
{
    /// <summary>
    /// Filters out APs that are mesh parent/child pairs on the same channel.
    /// Mesh pairs must be on the same channel to communicate, so it's expected.
    /// Returns APs that would cause actual co-channel interference.
    /// </summary>
    public static List<AccessPointSnapshot> FilterOutMeshPairs(
        List<AccessPointSnapshot> apsOnChannel,
        RadioBand band,
        int channel)
    {
        if (apsOnChannel.Count < 2)
            return apsOnChannel;

        // Build a set of mesh relationships for this band/channel
        var meshPairs = new HashSet<(string childMac, string parentMac)>();
        foreach (var ap in apsOnChannel)
        {
            if (ap.IsMeshChild &&
                !string.IsNullOrEmpty(ap.MeshParentMac) &&
                ap.MeshUplinkBand == band &&
                ap.MeshUplinkChannel == channel)
            {
                meshPairs.Add((ap.Mac.ToLowerInvariant(), ap.MeshParentMac.ToLowerInvariant()));
            }
        }

        if (!meshPairs.Any())
            return apsOnChannel;

        // Remove APs that are part of a mesh pair (both parent and child)
        // But only if ALL APs on this channel are part of mesh pairs
        var meshMacs = new HashSet<string>();
        foreach (var (child, parent) in meshPairs)
        {
            meshMacs.Add(child);
            meshMacs.Add(parent);
        }

        // Keep APs that are NOT in any mesh pair, plus any "leftover" mesh APs if they're also
        // on the channel with non-mesh APs
        var nonMeshAps = apsOnChannel
            .Where(ap => !meshMacs.Contains(ap.Mac.ToLowerInvariant()))
            .ToList();

        // If some APs remain that aren't in mesh pairs, return them
        // This handles the case where there's a mesh pair PLUS another AP on the channel
        if (nonMeshAps.Any())
        {
            return nonMeshAps;
        }

        // If ALL APs are part of mesh pairs, then there's no actual interference issue
        // (the mesh pairs need to be on the same channel)
        return new List<AccessPointSnapshot>();
    }
}
