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
/// (<see cref="RouterHostnameLocator"/>), else by GeoLite2. The hostname wins because GeoLite2 places
/// a transit router where its network is registered.
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

        foreach (var h in list)
        {
            if (result.ContainsKey(h.Address!)) continue;
            // The vantage hop is never checked against its own GeoLite2 location.
            var from = string.Equals(h.Address, nearest.Address, StringComparison.OrdinalIgnoreCase) ? null : vantage;
            if (RouterHostnameLocator.Locate(h.Hostname, h.MinRttMs, from) is { } named)
            {
                var city = named.City;
                result[h.Address!] = new HopLocationView(city.CountryCode,
                    $"{city.Name}, {CountryName(city.CountryCode)}",
                    $"From the router's hostname ({named.Token})");
            }
            else if (geoByAddress[h.Address!] is { CountryCode.Length: > 0 } g)
            {
                var country = CountryName(g.CountryCode!);
                result[h.Address!] = new HopLocationView(g.CountryCode,
                    string.IsNullOrEmpty(g.City) ? country : $"{g.City}, {country}",
                    "Where GeoLite2 places the address. For a transit router this can be where its network is registered, not where the router sits.");
            }
        }
        return result;
    }

    private static string CountryName(string code) =>
        CountryDisplay.CountryNames.TryGetValue(code, out var name) ? name : code.ToUpperInvariant();
}
