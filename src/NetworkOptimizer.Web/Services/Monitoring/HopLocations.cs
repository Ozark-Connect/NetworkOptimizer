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

        for (var i = 0; i < list.Count; i++)
        {
            var h = list[i];
            if (result.ContainsKey(h.Address!)) continue;
            var g = geoByAddress[h.Address!];
            if (named[i] is { } own)
            {
                result[h.Address!] = CityView(own.City, $"From the router's hostname ({own.Token})");
            }
            else if (NamedNeighbor(list, named, i, g) is { } near)
            {
                var source = $"Near {list[near.Index].Hostname}, {near.GapMs:0.0} ms away, whose hostname names {near.City.Name}.";
                result[h.Address!] = CityView(near.City, g.City is { Length: > 0 } geoCity
                    ? $"{source} GeoLite2's {geoCity} is too far for that round trip."
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
    /// A hop with no city of its own takes the city of the hostname-placed hop nearest it in the
    /// trace, when their round trips are within <see cref="NeighborGapMs"/>. GeoLite2's city wins
    /// unless it is too far from that neighbor for the round-trip gap.
    /// </summary>
    internal const double NeighborGapMs = 3;

    private static (int Index, RouterCity City, double GapMs)? NamedNeighbor(
        List<(string? Address, string? Hostname, double? MinRttMs)> list,
        RouterHostnameLocation?[] named, int i, GeoInfo geo)
    {
        if (list[i].MinRttMs is not double rtt) return null;
        (int Index, RouterCity City, double GapMs)? best = null;
        foreach (var j in new[] { Nearest(named, i, -1), Nearest(named, i, +1) })
        {
            if (j < 0 || list[j].MinRttMs is not double other) continue;
            var gap = Math.Abs(rtt - other);
            if (gap <= NeighborGapMs && (best == null || gap < best.Value.GapMs))
                best = (j, named[j]!.City, gap);
        }
        if (best is not { } b) return null;

        // A different country, or a GeoLite2 city the neighbor's round trip can reach, stands.
        if (geo.CountryCode is { Length: > 0 } cc && !string.Equals(cc, b.City.CountryCode, StringComparison.OrdinalIgnoreCase))
            return null;
        if (!string.IsNullOrEmpty(geo.City) && geo is { Latitude: double lat, Longitude: double lon }
            && RouterHostnameLocator.IsWithinReach(b.City, b.GapMs, (lat, lon)))
            return null;
        return b;
    }

    private static int Nearest(RouterHostnameLocation?[] named, int i, int step)
    {
        for (var j = i + step; j >= 0 && j < named.Length; j += step)
        {
            if (named[j] != null) return j;
        }
        return -1;
    }

    private static HopLocationView CityView(RouterCity city, string tooltip) =>
        new(city.CountryCode, $"{city.Name}, {CountryName(city.CountryCode)}", tooltip);

    private static string CountryName(string code) =>
        CountryDisplay.CountryNames.TryGetValue(code, out var name) ? name : code.ToUpperInvariant();
}
