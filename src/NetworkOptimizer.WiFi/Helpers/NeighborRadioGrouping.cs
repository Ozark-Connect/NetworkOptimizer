namespace NetworkOptimizer.WiFi.Helpers;

/// <summary>One observer's sighting of a neighbor BSSID.</summary>
/// <param name="ObserverIndex">Index of the site access point that heard it.</param>
/// <param name="Channel">The neighbor's channel.</param>
/// <param name="Width">The neighbor's channel width in MHz, when known.</param>
/// <param name="Signal">Signal at the observer, in dBm.</param>
/// <param name="Confidence">1.0 for a live sighting; lower for a remembered one.</param>
public readonly record struct NeighborSighting(int ObserverIndex, int Channel, int? Width, int Signal, double Confidence);

/// <summary>
/// Groups neighbor BSSIDs that one radio broadcasts. A radio carrying several SSIDs, hidden ones
/// included, sends one BSSID per SSID, and all of them share that radio's airtime. Counting each
/// as its own interferer multiplies one neighbor by its SSID count.
/// </summary>
public static class NeighborRadioGrouping
{
    /// <summary>How far apart two BSSIDs of one radio may read at the same observer, in dB.</summary>
    public const int SignalToleranceDb = 8;

    /// <summary>How far apart the last octets of two BSSIDs of one radio may sit (inclusive).</summary>
    public const int LastOctetSpan = SiteBssidLookup.DerivedLastOctetSpan;

    /// <summary>
    /// The BSSIDs partitioned into radios. Two BSSIDs join when their addresses are variants of one
    /// base address and every access point that heard both agrees on channel and signal. A pair no
    /// access point heard together stays apart.
    /// </summary>
    public static List<List<string>> Group(IReadOnlyDictionary<string, List<NeighborSighting>> sightingsByBssid)
    {
        var bssids = sightingsByBssid.Keys.ToList();
        var parent = Enumerable.Range(0, bssids.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);

        var macs = new byte[bssids.Count][];
        var best = new Dictionary<int, NeighborSighting>[bssids.Count];
        for (var i = 0; i < bssids.Count; i++)
        {
            macs[i] = SiteBssidLookup.TryParseMac(bssids[i], out var mac) ? mac : [];
            best[i] = BestPerObserver(sightingsByBssid[bssids[i]]);
        }

        // Only addresses sharing octets 2-5 can be variants, so pairs are compared inside that bucket.
        var buckets = Enumerable.Range(0, bssids.Count)
            .Where(i => macs[i].Length == 6)
            .GroupBy(i => (macs[i][1], macs[i][2], macs[i][3], macs[i][4]));
        foreach (var bucket in buckets)
        {
            var members = bucket.ToList();
            for (var a = 0; a < members.Count; a++)
            {
                for (var b = a + 1; b < members.Count; b++)
                {
                    var (i, j) = (members[a], members[b]);
                    if (AreAddressVariants(macs[i], macs[j]) && ObserversAgree(best[i], best[j]))
                        parent[Find(i)] = Find(j);
                }
            }
        }

        return Enumerable.Range(0, bssids.Count)
            .GroupBy(Find)
            .Select(g => g.Select(i => bssids[i]).ToList())
            .ToList();
    }

    /// <summary>
    /// True when two BSSIDs can come from one base address: octets 2-5 equal, last octets within
    /// <see cref="LastOctetSpan"/>, and first octets equal or at least one locally administered.
    /// </summary>
    public static bool AreAddressVariants(string bssidA, string bssidB) =>
        SiteBssidLookup.TryParseMac(bssidA, out var a) && SiteBssidLookup.TryParseMac(bssidB, out var b)
        && AreAddressVariants(a, b);

    private static bool AreAddressVariants(byte[] a, byte[] b)
    {
        for (var i = 1; i <= 4; i++)
            if (a[i] != b[i]) return false;
        if (Math.Abs(a[5] - b[5]) > LastOctetSpan) return false;
        // Bit 0x02 is locally administered. Two different universal addresses are two devices.
        return a[0] == b[0] || (a[0] & 0x02) != 0 || (b[0] & 0x02) != 0;
    }

    private static Dictionary<int, NeighborSighting> BestPerObserver(List<NeighborSighting> sightings) =>
        sightings
            .GroupBy(s => s.ObserverIndex)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.Confidence).ThenByDescending(s => s.Signal).First());

    private static bool ObserversAgree(Dictionary<int, NeighborSighting> a, Dictionary<int, NeighborSighting> b)
    {
        var common = 0;
        foreach (var (observer, sa) in a)
        {
            if (!b.TryGetValue(observer, out var sb)) continue;
            if (sa.Channel != sb.Channel || Math.Abs(sa.Signal - sb.Signal) > SignalToleranceDb) return false;
            common++;
        }
        return common > 0;
    }
}
