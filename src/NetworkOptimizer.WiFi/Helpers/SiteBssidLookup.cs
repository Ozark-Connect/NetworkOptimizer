using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Helpers;

/// <summary>The site access point and VAP that own one BSSID.</summary>
/// <param name="ApMac">The owning access point's MAC.</param>
/// <param name="ApName">The owning access point's name.</param>
/// <param name="Ssid">The VAP's ESSID; a mesh VAP's starts with <c>vwire-</c>.</param>
public sealed record SiteBssid(string ApMac, string? ApName, string Ssid);

/// <summary>
/// Resolves a scanned BSSID to the site access point that broadcasts it: a BSSID in an AP's
/// <c>vap_table</c>, or one derived from an AP's base MAC. The derived rule is needed because
/// mesh backhaul VAPs are missing from every <c>vap_table</c>. The scan's Ubiquiti flag is never
/// used: a neighbor's UniFi AP sets it too.
/// </summary>
public sealed class SiteBssidLookup
{
    /// <summary>Prefix UniFi gives a mesh backhaul VAP's ESSID.</summary>
    public const string MeshSsidPrefix = "vwire-";

    /// <summary>How far above the base MAC's last octet a derived BSSID may sit (inclusive).</summary>
    public const int DerivedLastOctetSpan = 15;

    private readonly Dictionary<string, SiteBssid> _vaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(byte[] Mac, string MacText, string? Name)> _aps = new();

    /// <summary>True when no AP or VAP has been added, so nothing can resolve.</summary>
    public bool IsEmpty => _vaps.Count == 0 && _aps.Count == 0;

    /// <summary>A lookup over every VAP and base MAC of every access point.</summary>
    public static SiteBssidLookup Build(IEnumerable<AccessPointSnapshot> aps)
    {
        var lookup = new SiteBssidLookup();
        foreach (var ap in aps)
        {
            lookup.AddAccessPoint(ap.Mac, ap.Name);
            foreach (var vap in ap.Vaps)
                lookup.AddVap(vap.Bssid, ap.Mac, ap.Name, vap.Essid);
        }
        return lookup;
    }

    /// <summary>Adds an access point's base MAC for the derived-BSSID rule.</summary>
    public void AddAccessPoint(string? apMac, string? apName)
    {
        if (string.IsNullOrEmpty(apMac) || !TryParseMac(apMac, out var mac)) return;
        _aps.Add((mac, apMac, apName));
    }

    /// <summary>Adds one VAP; the first owner of a BSSID wins.</summary>
    public void AddVap(string? bssid, string apMac, string? apName, string? essid)
    {
        if (string.IsNullOrEmpty(bssid) || string.IsNullOrEmpty(apMac)) return;
        _vaps.TryAdd(bssid, new SiteBssid(apMac, apName, essid ?? string.Empty));
    }

    /// <summary>
    /// The owner of <paramref name="bssid"/>: its VAP when listed, else the AP it derives from,
    /// with <paramref name="scannedSsid"/> as the SSID.
    /// </summary>
    public bool TryResolve(string? bssid, string? scannedSsid, out SiteBssid owner)
    {
        owner = null!;
        if (string.IsNullOrEmpty(bssid)) return false;
        if (_vaps.TryGetValue(bssid, out var vap))
        {
            owner = vap;
            return true;
        }
        if (_aps.Count == 0 || !TryParseMac(bssid, out var parsed)) return false;
        foreach (var ap in _aps)
        {
            if (!IsDerivedFromApMac(parsed, ap.Mac)) continue;
            owner = new SiteBssid(ap.MacText, ap.Name, scannedSsid ?? string.Empty);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Sets <see cref="NeighborNetwork.IsSiteBssid"/>, <see cref="NeighborNetwork.IsOwnNetwork"/>,
    /// and the owner fields when the neighbor resolves to a site AP. Leaves it untouched otherwise.
    /// </summary>
    public void Stamp(NeighborNetwork neighbor)
    {
        if (!TryResolve(neighbor.Bssid, neighbor.Ssid, out var owner)) return;
        neighbor.IsSiteBssid = true;
        neighbor.IsOwnNetwork = true;
        neighbor.OwnApMac = owner.ApMac;
        neighbor.OwnApName = owner.ApName;
        neighbor.OwnSsid = owner.Ssid;
    }

    /// <summary>
    /// True when <paramref name="bssid"/> is one UniFi derives from the AP's base MAC: octets 2-5
    /// equal, the last octet 0 to <see cref="DerivedLastOctetSpan"/> above the base's, and the first
    /// octet either equal or a locally-administered unicast variant.
    /// </summary>
    public static bool IsDerivedFromApMac(string bssid, string apMac) =>
        TryParseMac(bssid, out var b) && TryParseMac(apMac, out var a) && IsDerivedFromApMac(b, a);

    private static bool IsDerivedFromApMac(byte[] bssid, byte[] apMac)
    {
        for (var i = 1; i <= 4; i++)
            if (bssid[i] != apMac[i]) return false;
        var offset = bssid[5] - apMac[5];
        if (offset < 0 || offset > DerivedLastOctetSpan) return false;
        // Bit 0x02 is locally administered, 0x01 multicast.
        return bssid[0] == apMac[0] || (bssid[0] & 0x03) == 0x02;
    }

    /// <summary>True when the neighbor is a site mesh backhaul VAP, by its scanned or its owning ESSID.</summary>
    public static bool IsMeshBackhaul(NeighborNetwork neighbor) =>
        neighbor.Ssid.StartsWith(MeshSsidPrefix, StringComparison.OrdinalIgnoreCase)
        || (neighbor.OwnSsid?.StartsWith(MeshSsidPrefix, StringComparison.OrdinalIgnoreCase) ?? false);

    private static bool TryParseMac(string text, out byte[] mac)
    {
        mac = new byte[6];
        var hex = text.Replace(":", "").Replace("-", "").Replace(".", "");
        if (hex.Length != 12) return false;
        for (var i = 0; i < 6; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out mac[i]))
                return false;
        }
        return true;
    }
}
