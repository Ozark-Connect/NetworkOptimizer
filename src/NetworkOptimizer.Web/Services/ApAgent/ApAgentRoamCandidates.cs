using System.Globalization;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Reads and shapes 802.11k neighbor report elements for a BSS Transition candidate list.
///
/// Separate from the service because the candidate list is what actually steers a client, and it is
/// the one part of the feature that can be checked without a fleet of access points.
/// </summary>
public static class ApAgentRoamCandidates
{
    /// <summary>
    /// Rank of a band token, in either the agent's ("5") or UniFi's ("na") spelling. 0 means unknown,
    /// which never compares as better than a known band.
    /// </summary>
    public static int BandRank(string? band) => band switch
    {
        "6" or "6e" => 3,
        "5" or "na" => 2,
        "2.4" or "ng" => 1,
        _ => 0,
    };

    /// <summary>
    /// Band token of a neighbor report element, from its operating class. Per 802.11: 81-84 are
    /// 2.4 GHz, 115-130 are 5 GHz, 131-136 are 6 GHz.
    ///
    /// Hex layout: BSSID(6), BSSID info(4), operating class(1), channel(1) - so the band is already
    /// in what we forward and does not have to be tracked alongside it.
    /// </summary>
    public static string? BandOf(string? element)
    {
        if (element is not { Length: >= 22 }) return null;
        if (!int.TryParse(element.AsSpan(20, 2), NumberStyles.HexNumber, null, out var opClass))
            return null;

        return opClass switch
        {
            >= 81 and <= 84 => "2.4",
            >= 115 and <= 130 => "5",
            >= 131 and <= 136 => "6",
            _ => null,
        };
    }

    /// <summary>A neighbor report element as "bssid/band ch", for the candidate log.</summary>
    public static string Describe(string element)
    {
        if (element is not { Length: >= 24 }) return element;

        var bssid = string.Join(":", Enumerable.Range(0, 6).Select(i => element.Substring(i * 2, 2)));
        if (!int.TryParse(element.AsSpan(22, 2), NumberStyles.HexNumber, null, out var channel))
            return bssid;

        return $"{bssid}/{BandOf(element) ?? "?"}GHz ch{channel}";
    }

    /// <summary>
    /// One access point's other bands, best first, for a band move. An MLO client holds several
    /// bands at once, so every band it is already on is excluded rather than just the active one.
    /// </summary>
    public static List<string> OtherBands(IEnumerable<string> own, IReadOnlyCollection<string> currentBands)
        => own.Select(e => (Element: e, Band: BandOf(e)))
            .Where(x => x.Band != null && !currentBands.Contains(x.Band))
            .OrderByDescending(x => BandRank(x.Band))
            .Select(x => x.Element)
            .ToList();
}
