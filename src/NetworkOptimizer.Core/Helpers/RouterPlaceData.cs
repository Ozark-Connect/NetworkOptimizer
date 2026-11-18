using System.IO.Compression;
using System.Reflection;

namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// The generated place codes behind the curated <see cref="RouterLocationCities"/>: every large and
/// medium airport with an IATA code (OurAirports, public domain), and CLLI city codes derived from
/// GeoNames place names (CC BY 4.0). <see cref="RouterHostnameLocator"/> reads them only when the
/// curated list has no match. Built by <c>scripts/data/build-router-places.py</c>.
/// </summary>
public static class RouterPlaceData
{
    /// <summary>An airport and whether it is a large one.</summary>
    /// <param name="City">The city it serves.</param>
    /// <param name="Large">A large airport; a medium one is read only for operators known to use airport codes.</param>
    public sealed record Airport(RouterCity City, bool Large);

    private static readonly Lazy<(Dictionary<string, Airport> Airports, Dictionary<string, RouterCity> Clli)> Data = new(Load);

    /// <summary>Airports by IATA code, lower case.</summary>
    public static IReadOnlyDictionary<string, Airport> Airports => Data.Value.Airports;

    /// <summary>Derived CLLI place codes, the city's four letters and the state's two, lower case ("lnclne").</summary>
    public static IReadOnlyDictionary<string, RouterCity> Clli => Data.Value.Clli;

    /// <summary>
    /// Airport codes that are also router role words or network tags ("agr" aggregation, "ccr" core
    /// router), or that the curated list leaves out on purpose ("sat", "man"). Never read as airports.
    /// </summary>
    public static readonly IReadOnlySet<string> NotAirports = new HashSet<string>
    {
        "acc", "agg", "agr", "asw", "bar", "bbr", "bdr", "bng", "bpr", "cat", "ccr", "cdn", "cer", "chg", "cor", "cpe",
        "crs", "csw", "cxr", "dcr", "dsr", "dsw", "ear", "edg", "gen", "gwy", "ibr", "icr", "isg", "isp", "lag", "lan",
        "lns", "ltd", "mag", "man", "mar", "mpr", "msw", "nap", "net", "olt", "pop", "rev", "rtr", "rur", "sat", "sbr",
        "ser", "ter", "tgt", "top", "tor", "vpe", "vpn", "vps", "vrf", "wan",
    };

    private static (Dictionary<string, Airport>, Dictionary<string, RouterCity>) Load()
    {
        var airports = new Dictionary<string, Airport>(StringComparer.Ordinal);
        var clli = new Dictionary<string, RouterCity>(StringComparer.Ordinal);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("router-places.tsv.gz");
        if (stream == null) return (airports, clli);
        using var reader = new StreamReader(new GZipStream(stream, CompressionMode.Decompress));
        while (reader.ReadLine() is { } line)
        {
            var p = line.Split('\t');
            if (p[0] == "A" && p.Length == 8)
            {
                var city = new RouterCity(p[3], p[4], p[5], Coordinate(p[6]), Coordinate(p[7]), [p[1]], [], []);
                airports[p[1]] = new Airport(city, p[2] == "L");
            }
            else if (p[0] == "C" && p.Length == 7)
            {
                clli[p[1] + p[2]] = new RouterCity(p[3], p[2].ToUpperInvariant(), p[4], Coordinate(p[5]), Coordinate(p[6]), [], [p[1] + p[2]], []);
            }
        }
        return (airports, clli);
    }

    private static double Coordinate(string s) =>
        double.Parse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
}
