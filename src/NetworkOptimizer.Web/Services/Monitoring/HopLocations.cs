using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Threats.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>Where a traced hop is, as a trace table shows it.</summary>
/// <param name="CountryCode">ISO country code, for the flag.</param>
/// <param name="Text">"Dallas, United States", or the country alone.</param>
/// <param name="Tooltip">Where the location came from.</param>
public sealed record HopLocationView(string? CountryCode, string Text, string Tooltip);

/// <summary>
/// Places each hop of a trace: by the city its router hostname names when it names one
/// (<see cref="RouterHostnameLocator"/>), else by a hostname-placed neighbor a few ms away, else by
/// GeoLite2. The hostname wins because GeoLite2 places a transit router where its network is registered.
/// </summary>
public static class HopLocations
{
    /// <summary>
    /// Each hop's location, by address. Hops neither source places are left out. The hop GeoLite2
    /// places with the lowest round trip stands in for where the trace started, so the round-trip
    /// check can rule out a hostname city too far away.
    /// </summary>
    /// <param name="hops">The responding hops, in hop order.</param>
    /// <param name="geo">GeoLite2 lookup for an address (empty when the database is missing).</param>
    public static Dictionary<string, HopLocationView> Resolve(
        IEnumerable<(string? Address, string? Hostname, double? MinRttMs)> hops, Func<string, GeoInfo> geo)
    {
        var result = new Dictionary<string, HopLocationView>(StringComparer.OrdinalIgnoreCase);
        var list = hops.Where(h => !string.IsNullOrEmpty(h.Address)).ToList();
        var geoByAddress = new Dictionary<string, GeoInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in list)
        {
            if (!geoByAddress.ContainsKey(h.Address!)) geoByAddress[h.Address!] = geo(h.Address!);
        }

        // The placed hop with the lowest round trip is the nearest to where the trace started: usually
        // the access ISP's edge, whose GeoLite2 city is close to the site.
        var nearest = list
            .Where(h => geoByAddress[h.Address!] is { Latitude: not null, Longitude: not null })
            .OrderBy(h => h.MinRttMs ?? double.MaxValue)
            .FirstOrDefault();
        (double, double)? vantage = nearest.Address is { } at
            ? (geoByAddress[at].Latitude!.Value, geoByAddress[at].Longitude!.Value)
            : null;

        var named = new RouterHostnameLocation?[list.Count];
        for (var i = 0; i < list.Count; i++)
        {
            // The vantage hop is never checked against its own GeoLite2 location.
            var from = string.Equals(list[i].Address, nearest.Address, StringComparison.OrdinalIgnoreCase) ? null : vantage;
            named[i] = RouterHostnameLocator.Locate(list[i].Hostname, list[i].MinRttMs, from);
        }
        var bounded = BoundedByLaterHops(list);
        var anchors = Anchors(bounded, named, geoByAddress, nearest.Address);

        for (var i = 0; i < list.Count; i++)
        {
            var h = list[i];
            if (result.ContainsKey(h.Address!)) continue;
            var g = geoByAddress[h.Address!];
            if (named[i] is { } own)
            {
                result[h.Address!] = CityView(own.City.Name, own.City.CountryCode, $"From the router's hostname ({own.Token})");
            }
            // A private or CGNAT address can sit anywhere behind its network, so it never borrows a city.
            else if (anchors[i] == null && NetworkUtilities.IsPublicIpAddress(h.Address!)
                     && Neighbor(bounded, anchors, i, g) is { } near)
            {
                // The measured gap, as the table shows it; the bounded one only decides.
                var shown = Math.Abs((h.MinRttMs ?? 0) - (list[near.Index].MinRttMs ?? 0));
                var source = near.Anchor.FromHostname
                    ? $"{shown:0.0} ms from {near.Anchor.Label}, a {near.Anchor.City} router"
                    : $"{shown:0.0} ms from {near.Anchor.Label}, which GeoLite2 places in {near.Anchor.City}";
                result[h.Address!] = CityView(near.Anchor.City, near.Anchor.CountryCode, g.City is { Length: > 0 } geoCity
                    ? $"{source}. GeoLite2 says {geoCity}."
                    : source);
            }
            else if (g is { CountryCode.Length: > 0 })
            {
                var country = CountryName(g.CountryCode!);
                result[h.Address!] = new HopLocationView(g.CountryCode,
                    string.IsNullOrEmpty(g.City) ? country : $"{g.City}, {country}",
                    "Where GeoLite2 places the address. For a transit router this can be where its network is registered, not where the router sits.");
            }
        }
        return result;
    }

    /// <summary>
    /// A hop with no city of its own takes the city of the anchor nearest it in the trace, when their
    /// round trips are within <see cref="NeighborGapMs"/>. GeoLite2's city wins unless it is too far
    /// from that anchor for the round-trip gap.
    /// </summary>
    internal const double NeighborGapMs = 3;

    /// <summary>
    /// Each hop's round trip, lowered to the lowest of any later hop's: packets to a later hop pass
    /// this one, so its round trip bounds this hop's. A router can answer its own probes slower than
    /// the hop after it answers (6.4 ms, then 2.1 ms).
    /// </summary>
    private static List<(string? Address, string? Hostname, double? MinRttMs)> BoundedByLaterHops(
        List<(string? Address, string? Hostname, double? MinRttMs)> list)
    {
        var bounded = new List<(string? Address, string? Hostname, double? MinRttMs)>(list);
        double? lowest = null;
        for (var i = bounded.Count - 1; i >= 0; i--)
        {
            if (bounded[i].MinRttMs is double rtt) lowest = lowest is double l ? Math.Min(l, rtt) : rtt;
            bounded[i] = bounded[i] with { MinRttMs = lowest };
        }
        return bounded;
    }

    /// <summary>A hop other hops are placed against.</summary>
    private sealed record Anchor(string City, string CountryCode, double Latitude, double Longitude, string Label, bool FromHostname);

    /// <summary>
    /// The anchors, by hop: every hostname-placed hop, the vantage hop, and the last hop GeoLite2
    /// places in a city, when that last one is in reach of the vantage and no hostname-placed hop
    /// follows it. The hops between them give
    /// way to the anchors, not the other way round.
    /// </summary>
    private static Anchor?[] Anchors(
        List<(string? Address, string? Hostname, double? MinRttMs)> list, RouterHostnameLocation?[] named,
        Dictionary<string, GeoInfo> geo, string? vantageAddress)
    {
        var anchors = new Anchor?[list.Count];
        for (var i = 0; i < list.Count; i++)
        {
            if (named[i] is { } n)
            {
                anchors[i] = new Anchor(n.City.Name, n.City.CountryCode, n.City.Latitude, n.City.Longitude,
                    list[i].Hostname!.Split('.')[0], FromHostname: true);
            }
        }

        var vantageIndex = list.FindIndex(h => string.Equals(h.Address, vantageAddress, StringComparison.OrdinalIgnoreCase));
        if (vantageIndex < 0 || (anchors[vantageIndex] ?? GeoAnchor(list[vantageIndex], geo)) is not { } vantage)
            return anchors;
        anchors[vantageIndex] = vantage;

        // A hostname-placed hop after it outranks it, so it anchors nothing then.
        var lastIndex = list.FindLastIndex(h => GeoAnchor(h, geo) != null);
        if (lastIndex > vantageIndex && anchors[lastIndex] == null
            && !anchors.Skip(lastIndex + 1).Any(a => a is { FromHostname: true })
            && GeoAnchor(list[lastIndex], geo) is { } last
            && list[lastIndex].MinRttMs is double lastRtt && list[vantageIndex].MinRttMs is double vantageRtt
            && InReach(vantage, last.Latitude, last.Longitude, Math.Abs(lastRtt - vantageRtt)))
        {
            anchors[lastIndex] = last;
        }
        return anchors;
    }

    private static Anchor? GeoAnchor((string? Address, string? Hostname, double? MinRttMs) hop, Dictionary<string, GeoInfo> geo) =>
        geo[hop.Address!] is { City.Length: > 0, CountryCode.Length: > 0, Latitude: double lat, Longitude: double lon } g
            ? new Anchor(g.City!, g.CountryCode!, lat, lon, hop.Address!, FromHostname: false)
            : null;

    private static (Anchor Anchor, double GapMs, int Index)? Neighbor(
        List<(string? Address, string? Hostname, double? MinRttMs)> list, Anchor?[] anchors, int i, GeoInfo geo)
    {
        if (list[i].MinRttMs is not double rtt) return null;
        (Anchor Anchor, double GapMs, int Index)? best = null;
        var inGap = new List<string>();
        foreach (var j in new[] { Nearest(anchors, i, -1), Nearest(anchors, i, +1) })
        {
            if (j < 0 || list[j].MinRttMs is not double other) continue;
            var gap = Math.Abs(rtt - other);
            if (gap > NeighborGapMs) continue;
            inGap.Add(anchors[j]!.City);
            if (best == null || gap < best.Value.GapMs) best = (anchors[j]!, gap, j);
        }
        if (best is not { } b) return null;

        // A different country stands. So does a GeoLite2 city the anchor's round trip can reach,
        // unless the hop sits between two anchors in the same city.
        if (geo.CountryCode is { Length: > 0 } cc && !string.Equals(cc, b.Anchor.CountryCode, StringComparison.OrdinalIgnoreCase))
            return null;
        var between = inGap.Count == 2 && inGap[0] == inGap[1];
        if (!between && !string.IsNullOrEmpty(geo.City) && geo is { Latitude: double lat, Longitude: double lon }
            && InReach(b.Anchor, lat, lon, b.GapMs))
            return null;
        return b;
    }

    private static bool InReach(Anchor from, double latitude, double longitude, double rttGapMs) =>
        GeoDistance.Kilometers(from.Latitude, from.Longitude, latitude, longitude)
            <= rttGapMs * RouterHostnameLocator.KmPerMsRtt + RouterHostnameLocator.VantageSlackKm;

    private static int Nearest(Anchor?[] anchors, int i, int step)
    {
        for (var j = i + step; j >= 0 && j < anchors.Length; j += step)
        {
            if (anchors[j] != null) return j;
        }
        return -1;
    }

    private static HopLocationView CityView(string city, string countryCode, string tooltip) =>
        new(countryCode, $"{city}, {CountryName(countryCode)}", tooltip);

    private static string CountryName(string code) =>
        CountryDisplay.CountryNames.TryGetValue(code, out var name) ? name : code.ToUpperInvariant();
}
