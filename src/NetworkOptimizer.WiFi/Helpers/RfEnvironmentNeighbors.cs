using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Helpers;

/// <summary>
/// One BSSID on the RF Environment page, de-duplicated across every access point that heard it.
/// </summary>
public sealed class RfNeighborRow
{
    /// <summary>The strongest sighting of the BSSID.</summary>
    public NeighborNetwork Network { get; set; } = null!;

    /// <summary>MAC of the access point behind the strongest sighting.</summary>
    public string ApMac { get; set; } = "";

    /// <summary>Name of the access point behind the strongest sighting.</summary>
    public string ApName { get; set; } = "";

    /// <summary>Band of the scan the strongest sighting came from.</summary>
    public RadioBand Band { get; set; }

    /// <summary>Names of every access point that heard the BSSID.</summary>
    public List<string> DetectedByAps { get; set; } = new();

    /// <summary>MACs (lowercase) of every access point that heard the BSSID.</summary>
    public List<string> DetectedByApMacs { get; set; } = new();

    /// <summary>Strongest signal per hearing access point MAC (lowercase), for the AP filter.</summary>
    public Dictionary<string, int> SignalByApMac { get; set; } = new();

    /// <summary>The site access point that owns the BSSID; null for an external neighbor.</summary>
    public SiteBssid? SiteAp { get; set; }

    /// <summary>True when any sighting, or the owning VAP, names the BSSID a site mesh backhaul VAP.</summary>
    public bool IsMeshBackhaul { get; set; }
}

/// <summary>
/// Splits the scan results RF Environment shows into external neighbors and the site's own
/// access points heard by their siblings. With no site BSSIDs in the scans, the external list is
/// every neighbor, exactly as before the split.
/// </summary>
public static class RfEnvironmentNeighbors
{
    /// <summary>
    /// Every BSSID that is not one of the site's, de-duplicated by BSSID. These feed the
    /// Neighboring Networks table, Channel Density, DFS status, cleanest channels, and
    /// recommendations.
    /// </summary>
    public static List<RfNeighborRow> ExternalNeighbors(IEnumerable<ChannelScanResult> scans)
    {
        var scanList = scans as IReadOnlyCollection<ChannelScanResult> ?? scans.ToList();
        var owners = SiteOwners(scanList);
        return Flatten(scanList, (_, n) => !owners.ContainsKey(n.Bssid), owners);
    }

    /// <summary>
    /// The site's own BSSIDs as their sibling access points hear them. A sighting by the access
    /// point that owns the BSSID is dropped before anything is counted.
    /// </summary>
    public static List<RfNeighborRow> SiteAccessPoints(IEnumerable<ChannelScanResult> scans)
    {
        var scanList = scans as IReadOnlyCollection<ChannelScanResult> ?? scans.ToList();
        var owners = SiteOwners(scanList);
        if (owners.Count == 0) return new List<RfNeighborRow>();
        return Flatten(
            scanList,
            (scan, n) => owners.TryGetValue(n.Bssid, out var owner)
                && !string.Equals(owner.ApMac, scan.ApMac, StringComparison.OrdinalIgnoreCase),
            owners);
    }

    /// <summary>
    /// A BSSID is the site's when any sighting of it is flagged, so one unflagged copy cannot leak it.
    /// A hidden beacon's sighting carries no SSID, so the owner takes the first one that names it.
    /// </summary>
    private static Dictionary<string, SiteBssid> SiteOwners(IEnumerable<ChannelScanResult> scans)
    {
        var owners = new Dictionary<string, SiteBssid>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in scans.SelectMany(s => s.Neighbors))
        {
            if (!n.IsSiteBssid || string.IsNullOrEmpty(n.Bssid) || string.IsNullOrEmpty(n.OwnApMac)) continue;
            var ssid = n.OwnSsid ?? string.Empty;
            if (owners.TryGetValue(n.Bssid, out var known) && (known.Ssid.Length > 0 || ssid.Length == 0)) continue;
            owners[n.Bssid] = new SiteBssid(n.OwnApMac, n.OwnApName, ssid);
        }
        return owners;
    }

    private static List<RfNeighborRow> Flatten(
        IEnumerable<ChannelScanResult> scans,
        Func<ChannelScanResult, NeighborNetwork, bool> include,
        IReadOnlyDictionary<string, SiteBssid> owners)
    {
        // Channel 65535 (0xFFFF) and other out-of-range values come from the console; drop them.
        return scans
            .SelectMany(sr => sr.Neighbors
                .Where(n => n.Channel > 0 && n.Channel <= 233)
                .Where(n => include(sr, n))
                .Select(n => new RfNeighborRow
                {
                    Network = n,
                    ApMac = sr.ApMac,
                    ApName = sr.ApName ?? "Unknown AP",
                    Band = sr.Band
                }))
            .GroupBy(n => n.Network.Bssid.ToLowerInvariant())
            .Select(g =>
            {
                var strongest = g.OrderByDescending(n => n.Network.Signal ?? -100).First();
                strongest.DetectedByAps = g.Select(n => n.ApName).Distinct().ToList();
                strongest.DetectedByApMacs = g.Select(n => n.ApMac.ToLowerInvariant()).Distinct().ToList();
                strongest.SignalByApMac = g
                    .GroupBy(n => n.ApMac.ToLowerInvariant())
                    .ToDictionary(
                        apGroup => apGroup.Key,
                        apGroup => apGroup.Max(n => n.Network.Signal ?? -100));
                strongest.SiteAp = owners.TryGetValue(strongest.Network.Bssid, out var owner) ? owner : null;
                strongest.IsMeshBackhaul = g.Any(n => SiteBssidLookup.IsMeshBackhaul(n.Network))
                    || (owner?.Ssid.StartsWith(SiteBssidLookup.MeshSsidPrefix, StringComparison.OrdinalIgnoreCase) ?? false);
                return strongest;
            })
            .ToList();
    }
}
