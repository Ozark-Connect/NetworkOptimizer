using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Helpers;

/// <summary>The site access point and VAP that own one BSSID.</summary>
/// <param name="ApMac">The owning access point's MAC.</param>
/// <param name="ApName">The owning access point's name.</param>
/// <param name="Ssid">The VAP's ESSID; a mesh VAP's starts with <c>vwire-</c>.</param>
public sealed record SiteBssid(string ApMac, string? ApName, string Ssid);

/// <summary>
/// Resolves a scanned BSSID to the site access point that broadcasts it, from the APs'
/// <c>vap_table</c>. Membership there is the only own-network signal: the scan's Ubiquiti flag
/// is set for a neighbor's UniFi AP and missing on a scan radio's sibling entries.
/// </summary>
public static class SiteBssidLookup
{
    /// <summary>Prefix UniFi gives a mesh backhaul VAP's ESSID.</summary>
    public const string MeshSsidPrefix = "vwire-";

    /// <summary>BSSID (case-insensitive) to its owner, over every VAP of every access point.</summary>
    public static Dictionary<string, SiteBssid> Build(IEnumerable<AccessPointSnapshot> aps)
    {
        var lookup = new Dictionary<string, SiteBssid>(StringComparer.OrdinalIgnoreCase);
        foreach (var ap in aps)
        {
            foreach (var vap in ap.Vaps)
                Add(lookup, vap.Bssid, ap.Mac, ap.Name, vap.Essid);
        }
        return lookup;
    }

    /// <summary>Adds one VAP; the first owner of a BSSID wins.</summary>
    public static void Add(Dictionary<string, SiteBssid> lookup, string? bssid, string apMac, string? apName, string? essid)
    {
        if (string.IsNullOrEmpty(bssid) || string.IsNullOrEmpty(apMac)) return;
        lookup.TryAdd(bssid, new SiteBssid(apMac, apName, essid ?? string.Empty));
    }

    /// <summary>
    /// Sets <see cref="NeighborNetwork.IsSiteBssid"/> and the owner fields when the neighbor's
    /// BSSID is in <paramref name="lookup"/>. Leaves the neighbor untouched otherwise.
    /// </summary>
    public static void Stamp(NeighborNetwork neighbor, IReadOnlyDictionary<string, SiteBssid>? lookup)
    {
        if (lookup == null || string.IsNullOrEmpty(neighbor.Bssid)) return;
        if (!lookup.TryGetValue(neighbor.Bssid, out var owner)) return;
        neighbor.IsSiteBssid = true;
        neighbor.OwnApMac = owner.ApMac;
        neighbor.OwnApName = owner.ApName;
        neighbor.OwnSsid = owner.Ssid;
    }

    /// <summary>True when the neighbor is a site mesh backhaul VAP, by its scanned or its owning ESSID.</summary>
    public static bool IsMeshBackhaul(NeighborNetwork neighbor) =>
        neighbor.Ssid.StartsWith(MeshSsidPrefix, StringComparison.OrdinalIgnoreCase)
        || (neighbor.OwnSsid?.StartsWith(MeshSsidPrefix, StringComparison.OrdinalIgnoreCase) ?? false);
}
