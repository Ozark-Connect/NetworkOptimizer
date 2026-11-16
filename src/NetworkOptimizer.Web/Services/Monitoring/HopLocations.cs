using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Threats.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>Where a traced hop is, as a trace table shows it.</summary>
/// <param name="CountryCode">ISO country code, for the flag.</param>
/// <param name="Text">"Dallas, United States", or the country alone.</param>
/// <param name="Tooltip">Where the location came from.</param>
/// <param name="City">The city alone, when the hop is placed in one.</param>
/// <param name="Latitude">The placed city's latitude, in degrees; null when only the country is known.</param>
/// <param name="Longitude">The placed city's longitude, in degrees.</param>
/// <param name="Source">Which evidence placed the hop.</param>
public sealed record HopLocationView(
    string? CountryCode, string Text, string Tooltip,
    string? City = null, double? Latitude = null, double? Longitude = null,
    HopLocationSource Source = HopLocationSource.Country);

/// <summary>What placed a hop, strongest first.</summary>
public enum HopLocationSource
{
    /// <summary>The city someone set for the address (<see cref="IHopLocationOverrideService"/>).</summary>
    Set,

    /// <summary>The city its router hostname names.</summary>
    Hostname,

    /// <summary>The city of an anchor hop a few ms away.</summary>
    Neighbor,

    /// <summary>GeoLite2's city for the address.</summary>
    GeoLite2,

    /// <summary>The country only: no city, or a GeoLite2 city the round trip rules out.</summary>
    Country,
}

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
    /// <param name="set">Cities set by hand, by address. They win over every other source and anchor
    /// the hops around them, and nothing rules them out.</param>
    public static Dictionary<string, HopLocationView> Resolve(
        IEnumerable<(string? Address, string? Hostname, double? MinRttMs)> hops, Func<string, GeoInfo> geo,
        IReadOnlyDictionary<string, HopLocationSetting>? set = null)
    {
        var result = new Dictionary<string, HopLocationView>(StringComparer.OrdinalIgnoreCase);
        var list = hops.Where(h => !string.IsNullOrEmpty(h.Address)).ToList();
        var geoByAddress = new Dictionary<string, GeoInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in list)
        {
            if (!geoByAddress.ContainsKey(h.Address!)) geoByAddress[h.Address!] = geo(h.Address!);
        }

        // The placed hop with the lowest round trip is the nearest to where the trace started: usually
        // the access ISP's edge, whose GeoLite2 city is close to the site. A country-only location is
        // the country's center, which says nothing about where the trace started.
        var nearest = list
            .Where(h => geoByAddress[h.Address!] is { City.Length: > 0, Latitude: not null, Longitude: not null })
            .OrderBy(h => h.MinRttMs ?? double.MaxValue)
            .FirstOrDefault();
        // A city set by hand for that hop stands in for GeoLite2's.
        (double, double)? vantage = nearest.Address is not { } at ? null
            : set != null && set.TryGetValue(at, out var setAt) ? (setAt.Latitude, setAt.Longitude)
            : (geoByAddress[at].Latitude!.Value, geoByAddress[at].Longitude!.Value);

        var pinned = new HopLocationSetting?[list.Count];
        var named = new RouterHostnameLocation?[list.Count];
        for (var i = 0; i < list.Count; i++)
        {
            if (set != null && set.TryGetValue(list[i].Address!, out var s))
            {
                pinned[i] = s;
                continue;
            }
            // The vantage hop is never checked against its own GeoLite2 location.
            var from = string.Equals(list[i].Address, nearest.Address, StringComparison.OrdinalIgnoreCase) ? null : vantage;
            named[i] = RouterHostnameLocator.Locate(list[i].Hostname, list[i].MinRttMs, from);
        }
        var bounded = BoundedByLaterHops(list);
        var anchors = Anchors(bounded, named, pinned, geoByAddress, nearest.Address);

        for (var i = 0; i < list.Count; i++)
        {
            var h = list[i];
            if (result.ContainsKey(h.Address!)) continue;
            var g = geoByAddress[h.Address!];
            if (pinned[i] is { } p)
            {
                result[h.Address!] = CityView(p.City, p.CountryCode,
                    string.IsNullOrEmpty(p.SetBy) ? "Set by hand" : $"Set by {p.SetBy}",
                    p.Latitude, p.Longitude, HopLocationSource.Set);
            }
            else if (named[i] is { } own)
            {
                result[h.Address!] = CityView(own.City.Name, own.City.CountryCode, $"From the router's hostname ({own.Token})",
                    own.City.Latitude, own.City.Longitude, HopLocationSource.Hostname);
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
                // The borrowing hop is near the anchor, not at it: shown at the hub the anchor's suburb belongs to.
                var hub = Hub(near.Anchor);
                if (hub != null) source += $", near {hub.Name}";
                var geoSays = g.City is { Length: > 0 } geoCity ? geoCity
                    : g.CountryCode is { Length: > 0 } geoCountry && !string.Equals(geoCountry, near.Anchor.CountryCode, StringComparison.OrdinalIgnoreCase)
                        ? CountryName(geoCountry)
                        : null;
                result[h.Address!] = CityView(hub?.Name ?? near.Anchor.City, near.Anchor.CountryCode,
                    geoSays != null ? $"{source}. GeoLite2 says {geoSays}." : source,
                    hub?.Latitude ?? near.Anchor.Latitude, hub?.Longitude ?? near.Anchor.Longitude, HopLocationSource.Neighbor);
            }
            // GeoLite2's location, city or country alone, cannot fit the round trip from an anchor:
            // the country stays when it is the anchor's, and otherwise the hop has no location.
            else if (g is { CountryCode.Length: > 0 } && OutOfReach(bounded, anchors, i, g) is { } far)
            {
                var shown = Math.Abs((h.MinRttMs ?? 0) - (list[far.Index].MinRttMs ?? 0));
                var says = string.IsNullOrEmpty(g.City) ? CountryName(g.CountryCode!) : g.City;
                var tooltip = $"GeoLite2 says {says}: too far from {far.Anchor.City} for {shown:0.0} ms.";
                result[h.Address!] = string.Equals(g.CountryCode, far.Anchor.CountryCode, StringComparison.OrdinalIgnoreCase)
                    ? new HopLocationView(g.CountryCode, CountryName(g.CountryCode!), tooltip)
                    : new HopLocationView(null, "-", tooltip);
            }
            else if (g is { CountryCode.Length: > 0 })
            {
                var country = CountryName(g.CountryCode!);
                var hasCity = !string.IsNullOrEmpty(g.City);
                result[h.Address!] = new HopLocationView(g.CountryCode,
                    hasCity ? $"{g.City}, {country}" : country,
                    "Where GeoLite2 places the address. For a transit router this can be where its network is registered, not where the router sits.",
                    hasCity ? g.City : null, hasCity ? g.Latitude : null, hasCity ? g.Longitude : null,
                    hasCity ? HopLocationSource.GeoLite2 : HopLocationSource.Country);
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
    private sealed record Anchor(string City, string CountryCode, double Latitude, double Longitude, string Label, bool FromHostname,
        int AccuracyKm = 0, bool Set = false);

    /// <summary>
    /// The anchors, by hop: every hop with a city set by hand, every hostname-placed hop, the vantage
    /// hop, and the last hop GeoLite2 places in a city, when that last one is in reach of the vantage
    /// and no hostname-placed hop follows it. The hops between them give way to the anchors, not the
    /// other way round. Clears <paramref name="named"/> for a hostname city the last anchor rules out.
    /// </summary>
    private static Anchor?[] Anchors(
        List<(string? Address, string? Hostname, double? MinRttMs)> list, RouterHostnameLocation?[] named,
        HopLocationSetting?[] pinned, Dictionary<string, GeoInfo> geo, string? vantageAddress)
    {
        var anchors = new Anchor?[list.Count];
        for (var i = 0; i < list.Count; i++)
        {
            if (pinned[i] is { } p)
            {
                var label = string.IsNullOrEmpty(list[i].Hostname) ? list[i].Address! : list[i].Hostname!.Split('.')[0];
                anchors[i] = new Anchor(p.City, p.CountryCode, p.Latitude, p.Longitude, label, FromHostname: true, Set: true);
            }
            // An exchange's name places its own hop but anchors nothing: it names the exchange fabric,
            // and the member answering on it can sit far from it.
            else if (named[i] is { IsExchange: false } n)
            {
                anchors[i] = new Anchor(n.City.Name, n.City.CountryCode, n.City.Latitude, n.City.Longitude,
                    list[i].Hostname!.Split('.')[0], FromHostname: true);
            }
        }

        var vantageIndex = list.FindIndex(h => string.Equals(h.Address, vantageAddress, StringComparison.OrdinalIgnoreCase));
        if (vantageIndex < 0 || (anchors[vantageIndex] ?? GeoAnchor(list[vantageIndex], geo)) is not { } vantage)
            return anchors;
        anchors[vantageIndex] = vantage;

        // A hostname-placed hop after it outranks it, so it anchors nothing then. It must sit in the
        // destination's network (the last hop's ASN, when both are known): a transit hop's GeoLite2
        // city can be far from the router (a Dallas Cox hop placed in Warner Robins, GA).
        var lastIndex = list.FindLastIndex(h => GeoAnchor(h, geo) != null);
        var destinationAsn = list.Count > 0 ? geo[list[^1].Address!].Asn : null;
        if (lastIndex > vantageIndex && anchors[lastIndex] == null
            && (destinationAsn == null || geo[list[lastIndex].Address!].Asn is not int lastAsn || lastAsn == destinationAsn)
            && !anchors.Skip(lastIndex + 1).Any(a => a is { FromHostname: true })
            && GeoAnchor(list[lastIndex], geo) is { } last
            && list[lastIndex].MinRttMs is double lastRtt && list[vantageIndex].MinRttMs is double vantageRtt
            && InReach(vantage, last.Latitude, last.Longitude, Math.Abs(lastRtt - vantageRtt), last.AccuracyKm))
        {
            anchors[lastIndex] = last;

            // A hostname city the end of the trace cannot reach in the round-trip gap is ruled out,
            // as the vantage rules out one too far from the start.
            for (var j = vantageIndex + 1; j < lastIndex; j++)
            {
                if (anchors[j] is not { FromHostname: true, Set: false } a || list[j].MinRttMs is not double rtt) continue;
                if (InReach(last, a.Latitude, a.Longitude, Math.Abs(lastRtt - rtt), a.AccuracyKm)) continue;
                anchors[j] = null;
                named[j] = null;
            }
        }
        return anchors;
    }

    private static Anchor? GeoAnchor((string? Address, string? Hostname, double? MinRttMs) hop, Dictionary<string, GeoInfo> geo) =>
        geo[hop.Address!] is { City.Length: > 0, CountryCode.Length: > 0, Latitude: double lat, Longitude: double lon } g
            ? new Anchor(g.City!, g.CountryCode!, lat, lon, hop.Address!, FromHostname: false, g.AccuracyRadiusKm ?? 0)
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

        // A different country stands when GeoLite2's location for it fits the round trip, or has none.
        // A GeoLite2 city the anchor's round trip can reach stands too, unless the hop sits between
        // two anchors in the same city.
        var accuracy = geo.AccuracyRadiusKm ?? 0;
        var located = geo is { Latitude: not null, Longitude: not null };
        var fits = located && InReach(b.Anchor, geo.Latitude!.Value, geo.Longitude!.Value, b.GapMs, accuracy);
        if (geo.CountryCode is { Length: > 0 } cc && !string.Equals(cc, b.Anchor.CountryCode, StringComparison.OrdinalIgnoreCase)
            && (!located || fits))
            return null;
        var between = inGap.Count == 2 && inGap[0] == inGap[1];
        if (!between && !string.IsNullOrEmpty(geo.City) && fits)
            return null;
        return b;
    }

    // The nearest anchor on either side that GeoLite2's city is too far from for the round-trip gap.
    private static (Anchor Anchor, int Index)? OutOfReach(
        List<(string? Address, string? Hostname, double? MinRttMs)> list, Anchor?[] anchors, int i, GeoInfo geo)
    {
        if (anchors[i] != null || list[i].MinRttMs is not double rtt || geo is not { Latitude: double lat, Longitude: double lon })
            return null;
        foreach (var j in new[] { Nearest(anchors, i, -1), Nearest(anchors, i, +1) })
        {
            if (j < 0 || list[j].MinRttMs is not double other) continue;
            if (!InReach(anchors[j]!, lat, lon, Math.Abs(rtt - other), geo.AccuracyRadiusKm ?? 0)) return (anchors[j]!, j);
        }
        return null;
    }

    /// <summary>How far an anchor's city can be from a hub and still count as its suburb.</summary>
    internal const double HubRadiusKm = 60;

    // The nearest listed city with an airport or operator code, when the anchor's own city is a
    // different place within HubRadiusKm of it (Tucker to Atlanta). Null when the anchor is a hub.
    private static RouterCity? Hub(Anchor anchor)
    {
        var hub = RouterLocationCities.All
            .Where(c => c.Codes.Length > 0 && string.Equals(c.CountryCode, anchor.CountryCode, StringComparison.OrdinalIgnoreCase))
            .Select(c => (City: c, Km: GeoDistance.Kilometers(anchor.Latitude, anchor.Longitude, c.Latitude, c.Longitude)))
            .Where(x => x.Km <= HubRadiusKm)
            .OrderBy(x => x.Km)
            .Select(x => x.City)
            .FirstOrDefault();
        return hub == null || hub.Name == anchor.City ? null : hub;
    }

    // Both GeoLite2 accuracy radii count toward the reach: either location can be off by that much.
    private static bool InReach(Anchor from, double latitude, double longitude, double rttGapMs, int accuracyKm) =>
        GeoDistance.Kilometers(from.Latitude, from.Longitude, latitude, longitude) - from.AccuracyKm - accuracyKm
            <= rttGapMs * RouterHostnameLocator.KmPerMsRtt + RouterHostnameLocator.VantageSlackKm;

    private static int Nearest(Anchor?[] anchors, int i, int step)
    {
        for (var j = i + step; j >= 0 && j < anchors.Length; j += step)
        {
            if (anchors[j] != null) return j;
        }
        return -1;
    }

    private static HopLocationView CityView(
        string city, string countryCode, string tooltip, double latitude, double longitude, HopLocationSource source) =>
        new(countryCode, $"{city}, {CountryName(countryCode)}", tooltip, city, latitude, longitude, source);

    private static string CountryName(string code) =>
        CountryDisplay.CountryNames.TryGetValue(code, out var name) ? name : code.ToUpperInvariant();
}
