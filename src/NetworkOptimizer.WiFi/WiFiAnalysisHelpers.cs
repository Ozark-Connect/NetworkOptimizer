using System.Net;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Services;

namespace NetworkOptimizer.WiFi;

/// <summary>
/// Shared helper methods for WiFi analysis components.
/// </summary>
public static class WiFiAnalysisHelpers
{
    /// <summary>
    /// Whether UniFi's "Auto" TX power mode does real auto-power-leveling.
    /// Currently false because Auto is effectively just "High" in UniFi Network.
    /// Set to true once UniFi implements actual automatic power adjustment.
    /// </summary>
    public static bool SupportsAutoPowerLeveling => false;

    /// <summary>
    /// Sort access points by IP address (ascending, proper numeric sorting).
    /// APs without valid IPs are placed at the end.
    /// </summary>
    public static List<AccessPointSnapshot> SortByIp(IEnumerable<AccessPointSnapshot> aps)
    {
        return aps
            .OrderBy(ap =>
            {
                if (IPAddress.TryParse(ap.Ip, out var ip))
                {
                    var bytes = ip.GetAddressBytes();
                    if (bytes.Length == 4)
                        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
                }
                return uint.MaxValue;
            })
            .ToList();
    }

    /// <summary>
    /// Sort wireless clients by IP address (ascending, proper numeric sorting).
    /// Clients without valid IPs are placed at the end.
    /// </summary>
    public static List<WirelessClientSnapshot> SortByIp(IEnumerable<WirelessClientSnapshot> clients)
    {
        return clients
            .OrderBy(c =>
            {
                if (!string.IsNullOrEmpty(c.Ip) && IPAddress.TryParse(c.Ip, out var ip))
                {
                    var bytes = ip.GetAddressBytes();
                    if (bytes.Length == 4)
                        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
                }
                return uint.MaxValue;
            })
            .ToList();
    }

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

    /// <summary>
    /// Filter APs on the same channel by propagation modeling. Removes APs whose signal
    /// doesn't reach any other AP on the channel (i.e., they're too far apart to interfere).
    /// APs not placed on the map are kept (assume interference).
    /// </summary>
    public static List<AccessPointSnapshot> FilterByPropagation(
        List<AccessPointSnapshot> aps,
        RadioBand band, int channel,
        ApPropagationContext propCtx,
        PropagationService propagationSvc)
    {
        var bandStr = band.ToPropagationBand();

        // Build PropagationAp with current TX power for each placed AP
        var placed = new List<(AccessPointSnapshot Snapshot, PropagationAp Prop)>();
        var unplaced = new List<AccessPointSnapshot>();

        foreach (var ap in aps)
        {
            var macLower = ap.Mac.ToLowerInvariant();
            if (propCtx.ApsByMac.TryGetValue(macLower, out var propAp))
            {
                // Override TX power from live radio data for this specific band/channel
                var radio = ap.Radios.FirstOrDefault(r => r.Band == band && r.Channel == channel);
                var txPower = radio?.TxPower ?? propAp.TxPowerDbm;
                var antennaGain = radio?.AntennaGain ?? propAp.AntennaGainDbi;

                var clone = new PropagationAp
                {
                    Mac = propAp.Mac,
                    Model = propAp.Model,
                    Latitude = propAp.Latitude,
                    Longitude = propAp.Longitude,
                    Floor = propAp.Floor,
                    TxPowerDbm = txPower,
                    AntennaGainDbi = antennaGain,
                    OrientationDeg = propAp.OrientationDeg,
                    MountType = propAp.MountType,
                    AntennaMode = propAp.AntennaMode
                };
                placed.Add((ap, clone));
            }
            else
            {
                unplaced.Add(ap); // Not placed on map - keep in results (assume interference)
            }
        }

        if (placed.Count < 2)
        {
            // 0 or 1 placed APs - can't do pairwise check, return all
            return aps;
        }

        // Check all placed pairs
        var interferingMacs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < placed.Count; i++)
        {
            for (int j = i + 1; j < placed.Count; j++)
            {
                if (propagationSvc.DoApsInterfere(placed[i].Prop, placed[j].Prop, bandStr,
                    propCtx.WallsByFloor, propCtx.Buildings))
                {
                    interferingMacs.Add(placed[i].Snapshot.Mac);
                    interferingMacs.Add(placed[j].Snapshot.Mac);
                }
            }
        }

        // Return: unplaced APs (assume interference) + placed APs that actually interfere
        var result = new List<AccessPointSnapshot>(unplaced);
        result.AddRange(placed.Where(p => interferingMacs.Contains(p.Snapshot.Mac)).Select(p => p.Snapshot));
        return result;
    }
}
