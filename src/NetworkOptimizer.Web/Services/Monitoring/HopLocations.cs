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

    /// <summary>The city an earlier trace gave the address by its hostname or a nearby hop
    /// (<see cref="LearnedHopLocations"/>), where this trace has only GeoLite2 or nothing.</summary>
    Learned,

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
    /// <param name="overrides">Cities set by hand, by address (<see cref="IHopLocationOverrideService"/>). They win over every other source and anchor
    /// the hops around them, and nothing rules them out.</param>
    /// <param name="learned">Learned places (<see cref="LearnedHopLocations"/>): one replaces a hop this trace places only
    /// by GeoLite2 or the country, or not at all, when it fits this trace's round trips from every hop placed by hand,
    /// hostname, or a nearby hop. Never this trace's own evidence or an anycast address's, and it anchors nothing.</param>
    public static Dictionary<string, HopLocationView> Resolve(
        IEnumerable<(string? Address, string? Hostname, double? MinRttMs)> hops, Func<string, GeoInfo> geo,
        IReadOnlyDictionary<string, HopLocationSetting>? overrides = null,
        IReadOnlyDictionary<string, LearnedHop>? learned = null)
    {
        var result = new Dictionary<string, HopLocationView>(StringComparer.OrdinalIgnoreCase);
        var list = hops.Where(h => !string.IsNullOrEmpty(h.Address)).ToList();
        var geoByAddress = new Dictionary<string, GeoInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in list)
        {
            // An anycast address answers from its nearest site: GeoLite2's one place for it would
            // anchor the trace, and rule out true hostname cities, from the wrong metro. Its network
            // stays: it is still the destination's, which decides which hop may end the trace.
            if (geoByAddress.ContainsKey(h.Address!)) continue;
            var g = geo(h.Address!);
            geoByAddress[h.Address!] = AnycastAddresses.Contains(h.Address)
                ? new GeoInfo { Asn = g.Asn, AsnOrg = g.AsnOrg }
                : g;
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
            : overrides != null && overrides.TryGetValue(at, out var setAt) ? (setAt.Latitude, setAt.Longitude)
            : (geoByAddress[at].Latitude!.Value, geoByAddress[at].Longitude!.Value);

        var pinned = new HopLocationSetting?[list.Count];
        var named = new RouterHostnameLocation?[list.Count];
        for (var i = 0; i < list.Count; i++)
        {
            if (overrides != null && overrides.TryGetValue(list[i].Address!, out var s))
            {
                pinned[i] = s;
                continue;
            }
            // The vantage hop is never checked against its own GeoLite2 location.
            var from = string.Equals(list[i].Address, nearest.Address, StringComparison.OrdinalIgnoreCase) ? null : vantage;
            named[i] = RouterHostnameLocator.Locate(list[i].Hostname, list[i].MinRttMs, from);
        }
        var bounded = BoundedByLaterHops(list);
        var delayed = Delayed(list, bounded);
        var anchors = Anchors(bounded, named, pinned, geoByAddress, nearest.Address, delayed);

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
            else if (AnycastAddresses.Contains(h.Address))
            {
                result[h.Address!] = Anycast(bounded, anchors, i, h.Address!);
            }
            // A private or CGNAT address can sit anywhere behind its network, so it never borrows a city.
            else if (anchors[i] == null && NetworkUtilities.IsPublicIpAddress(h.Address!)
                     && Neighbor(bounded, delayed, anchors, i, g) is { } near)
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
            else if (g is { CountryCode.Length: > 0 } && OutOfReach(bounded, delayed, anchors, i, g) is { } far)
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
        if (learned != null) ApplyLearned(result, list, bounded, learned);
        return result;
    }

    // A learned city replaces only what this trace cannot place better itself, and only when this trace's own round
    // trips allow it: a handoff port learned under a far-end hostname once overrode the city its neighbors gave it.
    private static void ApplyLearned(Dictionary<string, HopLocationView> result,
        List<(string? Address, string? Hostname, double? MinRttMs)> list,
        List<(string? Address, string? Hostname, double? MinRttMs)> bounded, IReadOnlyDictionary<string, LearnedHop> learned)
    {
        var placed = new List<(double Lat, double Lon, double? Rtt, double SlackKm)>();
        for (var j = 0; j < list.Count; j++)
        {
            if (result.GetValueOrDefault(list[j].Address!) is { Latitude: double lat, Longitude: double lon } v
                && v.Source is HopLocationSource.Set or HopLocationSource.Hostname or HopLocationSource.Neighbor)
                placed.Add((lat, lon, bounded[j].MinRttMs,
                    v.Source == HopLocationSource.Neighbor ? RouterHostnameLocator.VantageSlackKm : CitySlackKm));
        }

        for (var i = 0; i < list.Count; i++)
        {
            var h = list[i];
            if (AnycastAddresses.Contains(h.Address) || !learned.TryGetValue(h.Address!, out var l)) continue;
            var now = result.GetValueOrDefault(h.Address!);
            var weaker = now == null || now.Source is HopLocationSource.GeoLite2 or HopLocationSource.Country;
            if (!weaker || string.Equals(now?.City, l.City, StringComparison.OrdinalIgnoreCase)) continue;
            if (bounded[i].MinRttMs is double rtt && placed.Any(p => p.Rtt is double other
                    && GeoDistance.Kilometers(l.Latitude, l.Longitude, p.Lat, p.Lon) > Math.Abs(rtt - other) * RouterHostnameLocator.KmPerMsRtt + p.SlackKm))
                continue;
            var how = l.Source == HopLocationSource.Hostname ? "its hostname" : "a hop a few ms away";
            var tooltip = $"Learned from an earlier trace, which placed it by {how} ({l.Detail}).";
            if (now != null) tooltip += $" This trace alone says {now.Text}.";
            result[h.Address!] = CityView(l.City, l.CountryCode, tooltip, l.Latitude, l.Longitude, HopLocationSource.Learned);
        }
    }

    /// <summary>
    /// The places in <paramref name="found"/> worth learning: transit and peering routers, a public unicast address
    /// placed by its hostname or a hop a few ms away. Never a hop on the destination's own network (a CDN edge, a
    /// cloud host, an anycast service), which can answer from another place for another vantage. A hand-set city is
    /// already kept for every trace, and GeoLite2's is no better next time.
    /// </summary>
    /// <param name="hops">The trace's responding hops, in hop order: the last one is the destination.</param>
    public static IEnumerable<LearnedHop> Learnable(IReadOnlyList<(string? Address, string? Hostname, double? MinRttMs)> hops,
        IReadOnlyDictionary<string, HopLocationView> found, Func<string, GeoInfo> geo, DateTime seenAt)
    {
        var list = hops.Where(h => !string.IsNullOrEmpty(h.Address)).ToList();
        if (list.Count < 2) yield break;
        var destination = list[^1].Address!;
        var destinationAsn = geo(destination).Asn;
        for (var i = 0; i < list.Count - 1; i++)
        {
            var h = list[i];
            var address = h.Address!;
            // A handoff port named for the network after it ("ae-0.<customer>.lsanca07") answers from the customer's
            // city, not the one it names: never learned from its name.
            var next = list[i + 1];
            if (found.GetValueOrDefault(address)?.Source == HopLocationSource.Hostname
                && NamesNetwork(h.Hostname, next.Hostname, geo(next.Address!).AsnOrg)) continue;
            if (!found.TryGetValue(address, out var v) || v.Source is not (HopLocationSource.Hostname or HopLocationSource.Neighbor)
                || v is not { City.Length: > 0, CountryCode.Length: > 0, Latitude: double lat, Longitude: double lon }
                || !NetworkUtilities.IsPublicIpAddress(address) || AnycastAddresses.Contains(address)
                || string.Equals(address, destination, StringComparison.OrdinalIgnoreCase)
                || (destinationAsn is int asn && geo(address).Asn == asn)) continue;
            yield return new LearnedHop(address, v.City!, v.CountryCode!, lat, lon, v.Source, v.Tooltip, seenAt);
        }
    }

    /// <summary>
    /// Where a trace from a WAN starts, for a route map's first point: the city set by hand for the
    /// WAN's address, else GeoLite2's city for it. Null when neither gives a city.
    /// </summary>
    public static (string City, string? CountryCode, double Latitude, double Longitude)? Start(
        string address, Func<string, GeoInfo> geo, IReadOnlyDictionary<string, HopLocationSetting>? overrides)
    {
        if (overrides != null && overrides.TryGetValue(address, out var s)) return (s.City, s.CountryCode, s.Latitude, s.Longitude);
        return geo(address) is { City.Length: > 0, Latitude: double lat, Longitude: double lon } g
            ? (g.City!, g.CountryCode, lat, lon)
            : null;
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
        int AccuracyKm = 0, bool SetByHand = false);

    /// <summary>
    /// The anchors, by hop: every hop with a city set by hand, every hostname-placed hop, the vantage
    /// hop, and the last hop GeoLite2 places in a city, when that last one is in reach of the vantage
    /// and no hostname-placed hop follows it. The hops between them give way to the anchors, not the
    /// other way round. Clears <paramref name="named"/> for a hostname city the last anchor rules out.
    /// </summary>
    private static Anchor?[] Anchors(
        List<(string? Address, string? Hostname, double? MinRttMs)> list, RouterHostnameLocation?[] named,
        HopLocationSetting?[] pinned, Dictionary<string, GeoInfo> geo, string? vantageAddress, bool[] delayed)
    {
        var anchors = new Anchor?[list.Count];
        for (var i = 0; i < list.Count; i++)
        {
            if (pinned[i] is { } p)
            {
                // A vantage reports a hop with no PTR record by its address as the hostname: its first label is "32".
                var host = list[i].Hostname;
                var label = string.IsNullOrEmpty(host) || System.Net.IPAddress.TryParse(host, out _) ? list[i].Address! : host.Split('.')[0];
                anchors[i] = new Anchor(p.City, p.CountryCode, p.Latitude, p.Longitude, label, FromHostname: true, SetByHand: true);
            }
            // An exchange's name places its own hop but anchors nothing: it names the exchange fabric,
            // and the member answering on it can sit far from it.
            else if (named[i] is { IsExchange: false } n)
            {
                anchors[i] = new Anchor(n.City.Name, n.City.CountryCode, n.City.Latitude, n.City.Longitude,
                    list[i].Hostname!.Split('.')[0], FromHostname: true);
            }
        }

        // A city set by hand whose round trip sits more than NeighborGapMs over an earlier anchor's in the same metro: the
        // extra is not distance (an MPLS hop set to Atlanta answering 25 ms past a Tucker router), so it lends nothing.
        for (var k = 0; k < list.Count; k++)
        {
            if (anchors[k] is not { SetByHand: true } set || delayed[k] || list[k].MinRttMs is not double rk) continue;
            for (var e = 0; e < k && !delayed[k]; e++)
            {
                if (anchors[e] is { } a && !delayed[e] && list[e].MinRttMs is double re && rk - re > NeighborGapMs
                    && GeoDistance.Kilometers(set.Latitude, set.Longitude, a.Latitude, a.Longitude) <= CitySlackKm)
                    delayed[k] = true;
            }
        }

        // A city set by hand rules out the hostname city of a hop right beside it (two hops, NeighborGapMs) that it cannot
        // reach in the gap, wherever it sits on the trace: a handoff port named for the far end of its link answers
        // beside the router it hands off to. Only beside it: a slow-answering router's bounded round trip says too little
        // about one further away (a Dallas router ruled out by Salt Lake City four hops on).
        var ruledOut = new List<int>();
        for (var k = 0; k < list.Count; k++)
        {
            if (anchors[k] is not { SetByHand: true } set || Unmeasured(set, delayed[k]) || list[k].MinRttMs is not double setRtt) continue;
            for (var j = Math.Max(0, k - 2); j <= Math.Min(list.Count - 1, k + 2); j++)
            {
                if (j == k || anchors[j] is not { FromHostname: true, SetByHand: false } a || list[j].MinRttMs is not double rtt
                    || Math.Abs(setRtt - rtt) > NeighborGapMs) continue;
                if (!InReach(set, a.Latitude, a.Longitude, Math.Abs(setRtt - rtt), a.AccuracyKm)) ruledOut.Add(j);
            }
        }
        foreach (var j in ruledOut.Distinct().Where(j => !Corroborated(list, anchors, j)).ToList())
        {
            anchors[j] = null;
            named[j] = null;
        }

        var vantageIndex = list.FindIndex(h => string.Equals(h.Address, vantageAddress, StringComparison.OrdinalIgnoreCase));
        if (vantageIndex < 0 || (anchors[vantageIndex] ?? GeoAnchor(list[vantageIndex], geo)) is not { } vantage)
            return anchors;
        anchors[vantageIndex] = vantage;

        // The final PoP, the router right before the destination, naming its city outranks the
        // destination's GeoLite2 city: the routers along the way can answer from elsewhere, but the
        // last one is where the destination is reached. GeoLite2's end then anchors nothing.
        // It still rules out a hostname city it cannot reach in the gap, as GeoLite2's end would: without this, setting
        // the final PoP's city skipped the check and a handoff port named for the far end of its link kept that city.
        if (FinalPopNamed(list, anchors, geo))
        {
            var popIndex = list.Count - 2;
            if (anchors[popIndex] is { } pop && list[popIndex].MinRttMs is double popRtt)
            {
                var far = new List<int>();
                for (var j = vantageIndex + 1; j < popIndex; j++)
                {
                    if (anchors[j] is not { FromHostname: true, SetByHand: false } a || list[j].MinRttMs is not double rtt) continue;
                    if (!InReach(pop, a.Latitude, a.Longitude, Math.Abs(popRtt - rtt), a.AccuracyKm)) far.Add(j);
                }
                if (!far.Any(j => Corroborated(list, anchors, j)))
                {
                    foreach (var j in far)
                    {
                        anchors[j] = null;
                        named[j] = null;
                    }
                }
            }
            return anchors;
        }

        // The last hop GeoLite2 places in a city anchors the end of the trace. A hostname-placed hop
        // after it outranks it, so it anchors nothing then. It must sit in the destination's network
        // (the last hop's ASN, when both are known): a transit hop's GeoLite2 city can be far from the
        // router (a Dallas Cox hop placed in Warner Robins, GA).
        var lastIndex = list.FindLastIndex(h => GeoAnchor(h, geo) != null);
        var destinationAsn = list.Count > 0 ? geo[list[^1].Address!].Asn : null;
        if (lastIndex > vantageIndex && anchors[lastIndex] == null
            && (destinationAsn == null || geo[list[lastIndex].Address!].Asn is not int lastAsn || lastAsn == destinationAsn)
            && !anchors.Skip(lastIndex + 1).Any(a => a is { FromHostname: true })
            && GeoAnchor(list[lastIndex], geo) is { } last
            && list[lastIndex].MinRttMs is double lastRtt && list[vantageIndex].MinRttMs is double vantageRtt
            && InReach(vantage, last.Latitude, last.Longitude, Math.Abs(lastRtt - vantageRtt), last.AccuracyKm))
        {
            // A hostname city the end of the trace cannot reach in the round-trip gap is ruled out,
            // as the vantage rules out one too far from the start: one interface can be named for
            // the far end of its link. A city a second hostname hop within NeighborGapMs agrees on is
            // not: then GeoLite2's end is the evidence that is wrong (an operator's block placed at
            // its headquarters), and it anchors nothing.
            var unreachable = new List<int>();
            for (var j = vantageIndex + 1; j < lastIndex; j++)
            {
                if (anchors[j] is not { FromHostname: true, SetByHand: false } a || list[j].MinRttMs is not double rtt) continue;
                if (!InReach(last, a.Latitude, a.Longitude, Math.Abs(lastRtt - rtt), a.AccuracyKm)) unreachable.Add(j);
            }
            if (unreachable.Any(j => Corroborated(list, anchors, j))) return anchors;

            anchors[lastIndex] = last;
            foreach (var j in unreachable)
            {
                anchors[j] = null;
                named[j] = null;
            }
        }
        return anchors;
    }

    /// <summary>
    /// The router right before the destination (the last responding hop before it) is placed by a
    /// city set by hand or one its hostname names, and the destination is not itself so placed.
    /// </summary>
    private static bool FinalPopNamed(List<(string? Address, string? Hostname, double? MinRttMs)> list, Anchor?[] anchors,
        Dictionary<string, GeoInfo> geo) =>
        list.Count >= 2 && anchors[^1] == null && anchors[^2] is { FromHostname: true, SetByHand: var setByHand }
        // Adjacent in time too: a destination tens of ms past it was reached somewhere else.
        && list[^1].MinRttMs is double end && list[^2].MinRttMs is double pop && Math.Abs(end - pop) <= NeighborGapMs
        // A port named for the network it hands off to is the far end of its link, not a PoP: an NTT
        // ae-0.<customer>.lsanca07 port answers from the customer's city, 900 km away. A city set by hand stands.
        && (setByHand || !NamesNetwork(list[^2].Hostname, list[^1].Hostname, geo[list[^1].Address!].AsnOrg));

    /// <summary>
    /// Whether a router's hostname names another network: a label part matching the other hop's own
    /// domain name ("examplenet" in core1.examplenet.net) or a word of its network's name.
    /// </summary>
    internal static bool NamesNetwork(string? hostname, string? otherHostname, string? otherAsnOrg)
    {
        if (string.IsNullOrEmpty(hostname)) return false;
        var labels = hostname.ToLowerInvariant().TrimEnd('.').Split('.');
        if (labels.Length < 3) return false;
        // The router's own operator (its last two labels) never counts.
        var own = labels[^2];
        var parts = labels[..^2].SelectMany(l => l.Split('-', '_')).Where(p => p.Length >= 4).ToHashSet();

        var names = new HashSet<string>();
        var other = otherHostname?.ToLowerInvariant().TrimEnd('.').Split('.');
        if (other is { Length: >= 2 }) names.Add(other[^2]);
        foreach (var word in (otherAsnOrg ?? "").ToLowerInvariant().Split(' ', ',', '.', '-', '/'))
            if (word.Length >= 4 && !GenericNetworkWords.Contains(word)) names.Add(word);
        names.Remove(own);
        return names.Any(parts.Contains);
    }

    // Words in many networks' names that say nothing about which network it is.
    private static readonly HashSet<string> GenericNetworkWords = new(StringComparer.Ordinal)
    {
        "communications", "communication", "network", "networks", "internet", "telecom", "telecommunications",
        "cable", "services", "service", "limited", "company", "group", "holdings", "technologies", "technology",
        "online", "global", "broadband", "fiber", "fibre", "wireless", "telephone", "systems", "solutions", "corp",
        "corporation", "level", "data", "cloud", "hosting", "core",
    };

    // Another hop within NeighborGapMs has a router hostname naming the same city as this one's.
    private static bool Corroborated(List<(string? Address, string? Hostname, double? MinRttMs)> list, Anchor?[] anchors, int i) =>
        anchors[i] is { FromHostname: true, SetByHand: false } a && list[i].MinRttMs is double rtt
        && anchors.Where((other, j) => j != i && other is { FromHostname: true, SetByHand: false } o
            && o.City == a.City && o.CountryCode == a.CountryCode
            && list[j].MinRttMs is double otherRtt && Math.Abs(otherRtt - rtt) <= NeighborGapMs).Any();

    private static Anchor? GeoAnchor((string? Address, string? Hostname, double? MinRttMs) hop, Dictionary<string, GeoInfo> geo) =>
        geo[hop.Address!] is { City.Length: > 0, CountryCode.Length: > 0, Latitude: double lat, Longitude: double lon } g
            ? new Anchor(g.City!, g.CountryCode!, lat, lon, hop.Address!, FromHostname: false, g.AccuracyRadiusKm ?? 0)
            : null;

    /// <summary>
    /// Hops whose round trip is not known to within <see cref="NeighborGapMs"/>: the own reply came back more than that
    /// after a later hop's (an MPLS tunnel returning it from its far end, or a router slow to answer), and the last hop
    /// measured before it leaves more than that below the ceiling the later hops set. A gap from such a hop can be off
    /// by the whole range (<see cref="Unmeasured"/>).
    /// </summary>
    private static bool[] Delayed(List<(string? Address, string? Hostname, double? MinRttMs)> list,
        List<(string? Address, string? Hostname, double? MinRttMs)> bounded)
    {
        var delayed = new bool[list.Count];
        double floor = 0;
        for (var i = 0; i < list.Count; i++)
        {
            if (bounded[i].MinRttMs is not double ceiling) continue;
            delayed[i] = list[i].MinRttMs is double own && own - ceiling > NeighborGapMs && ceiling - floor > NeighborGapMs;
            // Only a hop measured to within the gap sets the floor: a delayed one's round trip is as unknown as this one's.
            if (!delayed[i]) floor = ceiling;
        }
        return delayed;
    }

    // A city set by hand on a hop whose round trip is not its distance (Delayed, or far over an anchor in its own metro,
    // see Anchors) places that hop only: lent to its neighbors, it pulled the hops after it to the set city (an MPLS hop
    // set to Atlanta took three Little Rock routers along).
    // Hostname and GeoLite2 anchors keep lending: replayed congestion traces lost right cities when they stopped.
    private static bool Unmeasured(Anchor anchor, bool delayed) => delayed && anchor.SetByHand;

    private static (Anchor Anchor, double GapMs, int Index)? Neighbor(
        List<(string? Address, string? Hostname, double? MinRttMs)> list, bool[] delayed, Anchor?[] anchors, int i, GeoInfo geo)
    {
        if (list[i].MinRttMs is not double rtt) return null;
        (Anchor Anchor, double GapMs, int Index)? best = null;
        var inGap = new List<string>();
        foreach (var j in new[] { Nearest(anchors, i, -1), Nearest(anchors, i, +1) })
        {
            if (j < 0 || Unmeasured(anchors[j]!, delayed[j]) || list[j].MinRttMs is not double other) continue;
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
        List<(string? Address, string? Hostname, double? MinRttMs)> list, bool[] delayed, Anchor?[] anchors, int i, GeoInfo geo)
    {
        if (anchors[i] != null || list[i].MinRttMs is not double rtt || geo is not { Latitude: double lat, Longitude: double lon })
            return null;
        foreach (var j in new[] { Nearest(anchors, i, -1), Nearest(anchors, i, +1) })
        {
            if (j < 0 || Unmeasured(anchors[j]!, delayed[j]) || list[j].MinRttMs is not double other) continue;
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
            <= rttGapMs * RouterHostnameLocator.KmPerMsRtt + Slack(from);

    /// <summary>
    /// Slack on an anchor's reach for a router a few km from its city's center: a city set by hand
    /// or named by a router's hostname is exact. A GeoLite2 anchor keeps the vantage's wider slack.
    /// </summary>
    internal const double CitySlackKm = 100;

    private static double Slack(Anchor from) => from.FromHostname ? CitySlackKm : RouterHostnameLocator.VantageSlackKm;

    private const string AnycastNote =
        "Anycast: many sites answer this address, so GeoLite2's one place for it says nothing about which site did.";

    /// <summary>
    /// An anycast hop's metro, from its round trip past the last placed hop before it: that hop's own
    /// metro within <see cref="NeighborGapMs"/>, else the one interconnection metro
    /// (<see cref="AnycastAddresses.Metros"/>) the gap reaches. With no placed hop before it, or none
    /// or several metros in reach, it gets no location.
    /// </summary>
    private static HopLocationView Anycast(
        List<(string? Address, string? Hostname, double? MinRttMs)> list, Anchor?[] anchors, int i, string address)
    {
        var j = Nearest(anchors, i, -1);
        if (j < 0 || list[i].MinRttMs is not double rtt || list[j].MinRttMs is not double before)
            return new HopLocationView(null, "Anycast", AnycastNote);
        var anchor = anchors[j]!;
        var gap = Math.Max(0, rtt - before);
        var from = $"{gap:0.0} ms past {anchor.Label}, {(anchor.FromHostname ? "a" : "placed in")} {anchor.City}{(anchor.FromHostname ? " router" : "")}";
        // A few ms past a placed hop: the site that answered is in that hop's metro, shown at its hub
        // as a borrowing neighbor is.
        if (gap <= NeighborGapMs)
        {
            var hub = Hub(anchor);
            return CityView(hub?.Name ?? anchor.City, anchor.CountryCode, $"{AnycastNote} It answered {from}.",
                hub?.Latitude ?? anchor.Latitude, hub?.Longitude ?? anchor.Longitude, HopLocationSource.Neighbor);
        }
        // Only too far rules a metro out. A gap is no floor on the distance: a resolver slow to answer
        // its own probes, or a queue, adds ms without adding km, and the hop's own metro must stay in.
        var max = gap * RouterHostnameLocator.KmPerMsRtt + Slack(anchor) + anchor.AccuracyKm;
        var fits = AnycastAddresses.Metros
            .Select(m => RouterLocationCities.All.FirstOrDefault(c => c.Name == m.Name
                && string.Equals(c.CountryCode, m.CountryCode, StringComparison.OrdinalIgnoreCase)))
            .Where(c => c != null)
            .Select(c => (City: c!, Km: GeoDistance.Kilometers(anchor.Latitude, anchor.Longitude, c!.Latitude, c.Longitude)))
            .Where(x => x.Km <= max)
            .ToList();
        if (fits.Count == 0)
            return new HopLocationView(null, "Anycast", $"{AnycastNote} No interconnection metro fits {from}.");
        // Placed only when the round trip leaves one metro: picking among several is a guess, and a
        // wrong city reads worse than none.
        if (fits.Count > 1)
            return new HopLocationView(null, "Anycast",
                $"{AnycastNote} {from}, could be {string.Join(", ", fits.Select(f => f.City.Name))}.");
        var pick = fits[0].City;
        return CityView(pick.Name, pick.CountryCode, $"{AnycastNote} {from}, leaves {pick.Name} as the only metro in reach.",
            pick.Latitude, pick.Longitude, HopLocationSource.Neighbor);
    }

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
