namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// A mesh child's entries in its parent's downlink_table: the parent's end of the backhaul. Shared by the
/// Wi-Fi Optimizer and LAN Health so both ends of a mesh link resolve one way.
/// </summary>
public static class MeshDownlinks
{
    /// <summary>
    /// The parent's entries for <paramref name="childMac"/>, matched by serialno (or mld_mac when serialno is
    /// absent). An MLO backhaul is one is_mlo entry per link, all returned; a non-MLO child keeps its first entry.
    /// </summary>
    public static (List<DownlinkTableEntry> Entries, bool IsMlo) For(IEnumerable<DownlinkTableEntry>? table, string childMac)
    {
        var matches = (table ?? [])
            .Where(d => string.Equals((d.SerialNo ?? d.MldMac)?.Trim(), childMac.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        var mlo = matches.Where(e => e.IsMlo == true).ToList();
        return mlo.Count > 0 ? (mlo, true) : (matches.Take(1).ToList(), false);
    }
}