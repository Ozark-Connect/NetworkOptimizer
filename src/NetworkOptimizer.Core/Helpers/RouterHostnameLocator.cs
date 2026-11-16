namespace NetworkOptimizer.Core.Helpers;

/// <summary>How a hostname token named a city, weakest first.</summary>
public enum RouterLocationMatch
{
    /// <summary>A bare short code ("ash", "dfw").</summary>
    Code = 1,

    /// <summary>A short code followed by a site number ("atl01", "dfw02"), the usual shape of a location token.</summary>
    NumberedCode = 2,

    /// <summary>The city's name ("dallas1", "saltlakecity"), or a facility that stands for it.</summary>
    Name = 3,

    /// <summary>A six-letter CLLI place code ("dllstx09"), or a name confirmed by the region label after it.</summary>
    Exact = 4,
}

/// <summary>A city read out of a router hostname.</summary>
/// <param name="City">The city.</param>
/// <param name="Token">The hostname token that named it.</param>
/// <param name="Match">How strongly the token named it.</param>
public sealed record RouterHostnameLocation(RouterCity City, string Token, RouterLocationMatch Match)
{
    /// <summary>
    /// The name is an internet exchange's (an Equinix data center, an exchange's own domain): it names
    /// the exchange fabric, and the member answering on it can sit far from it.
    /// </summary>
    public bool IsExchange { get; init; }

    /// <summary>The scheme the token was read as.</summary>
    public RouterNamingScheme Scheme { get; init; }
}

/// <summary>
/// Places a router by the city its operator wrote into its hostname: CLLI codes ("dllstx09"), city
/// names ("atlanta2", "saltlakecity.ut"), and airport or operator codes ("atl01", "ash-bb2").
/// For a backbone router this beats an IP geolocation database, which places the address where its
/// block is registered.
/// </summary>
/// <remarks>
/// Given the hop's minimum round trip and where the trace started, a city farther away than light
/// in fiber covers in that time is ruled out. That is what keeps a short code honest: a network tag
/// that happens to be an airport code ("icn") cannot place a 16 ms hop in Seoul.
/// </remarks>
public static class RouterHostnameLocator
{
    /// <summary>
    /// Kilometers of distance per millisecond of round trip: light in fiber covers about 200 km per
    /// ms one way. Real paths are longer than a straight line, so this bound is generous.
    /// </summary>
    public const double KmPerMsRtt = 100;

    /// <summary>
    /// Added to every distance bound, for a vantage location that is itself approximate (an IP
    /// geolocation of the first public hop).
    /// </summary>
    public const double VantageSlackKm = 500;

    private const int MinCodeLength = 3;
    private const int MinNameLength = 4;
    private static readonly char[] Digits = "0123456789".ToCharArray();

    private static readonly System.Text.RegularExpressions.Regex CoxRouter =
        new("^([a-z0-9]{4})(?:bbr|bpr|dsr|hdr|tcr)[jc][0-9]{2}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex SiteLetterShape =
        new("^([a-z]{3})[0-9]{2}[a-z]$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex SiteRoleShape =
        new("^([a-z]{3})[0-9]{3}[a-z]{2}[0-9]{2}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly Dictionary<string, RouterCity[]> ByCode = Index(c => c.Codes);
    private static readonly Dictionary<string, RouterCity[]> ByClli = Index(c => c.Clli);
    private static readonly Dictionary<string, RouterCity[]> ByName = Index(c => c.Names);

    /// <summary>
    /// The city the hostname names, or null when it names none, or names two the round trip
    /// cannot tell apart.
    /// </summary>
    /// <param name="hostname">The router's PTR name.</param>
    /// <param name="minRttMs">The hop's minimum round trip, in ms; with <paramref name="vantage"/> it rules out cities too far away.</param>
    /// <param name="vantage">Roughly where the trace started, in degrees.</param>
    public static RouterHostnameLocation? Locate(
        string? hostname, double? minRttMs = null, (double Latitude, double Longitude)? vantage = null)
    {
        var candidates = Candidates(hostname);
        if (minRttMs is double rtt && vantage is { } from && rtt >= 0)
        {
            candidates = candidates.Where(c => IsWithinReach(c.City, rtt, from)).ToList();
        }
        if (candidates.Count == 0) return null;

        var best = candidates.Max(c => c.Match);
        var top = candidates.Where(c => c.Match == best).ToList();
        return top.Select(c => c.City).Distinct().Count() == 1 ? top[0] : null;
    }

    /// <summary>
    /// True when a hop in <paramref name="city"/> could answer in <paramref name="minRttMs"/> from
    /// <paramref name="vantage"/>.
    /// </summary>
    public static bool IsWithinReach(RouterCity city, double minRttMs, (double Latitude, double Longitude) vantage) =>
        GeoDistance.Kilometers(vantage.Latitude, vantage.Longitude, city.Latitude, city.Longitude)
            <= minRttMs * KmPerMsRtt + VantageSlackKm;

    /// <summary>
    /// Every city the hostname's tokens name, in hostname order. The last two labels (the
    /// operator's own domain) are never read.
    /// </summary>
    public static IReadOnlyList<RouterHostnameLocation> Candidates(string? hostname)
    {
        var found = new List<RouterHostnameLocation>();
        if (string.IsNullOrWhiteSpace(hostname)) return found;

        var labels = hostname.Trim().TrimEnd('.').ToLowerInvariant().Split('.');
        if (labels.Length < 3) return found;
        var read = labels.Length - 2;
        var domain = $"{labels[^2]}.{labels[^1]}";
        var op = RouterLocationCities.OperatorFor(domain);

        for (var i = 0; i < read; i++)
        {
            var parts = labels[i].Split('-', '_');
            for (var j = 0; j < parts.Length; j++)
            {
                // A two-letter part or label right after a token is its state or country ("dlls02-tx",
                // "ogden.ut"). Only numbers between them still count: "rb-dca-3.za".
                var onlyNumbersAfter = parts.Skip(j + 1).All(p => p.All(char.IsAsciiDigit));
                var qualifier = j + 1 < parts.Length && TwoLetters(parts[j + 1]) is { } next ? next
                    : onlyNumbersAfter && i + 1 < labels.Length ? TwoLetters(labels[i + 1])
                    : null;
                if (op?.NotPlaces is { } notPlaces && notPlaces.Contains(parts[j].Trim(Digits))) continue;
                if (!AddOwnCode(parts[j], op, found)) AddMatches(parts[j], qualifier, op, found);
                if (parts[j] is "eqix" or "equinix" && j + 1 < parts.Length) AddEquinix(parts[j + 1], found);
            }
            // The metro code can be the next label, as well as the next part ("eqix.da4.example.com").
            if (labels[i] is "eqix" or "equinix" && i + 1 < labels.Length - 2) AddEquinix(labels[i + 1], found);
        }

        // An exchange's own domain is the one operator domain that names a city.
        if (RouterLocationCities.ExchangeDomains.TryGetValue(domain, out var exchangeCity))
        {
            found.AddRange(RouterLocationCities.All.Where(c => c.Name == exchangeCity)
                .Select(c => new RouterHostnameLocation(c, labels[^2], RouterLocationMatch.Name)
                    { IsExchange = true, Scheme = RouterNamingScheme.Exchange }));
        }

        // A known operator's names are read with its own schemes and codes alone; an exchange's name is read for all.
        return op == null
            ? found
            : found.Where(c => c.Scheme is RouterNamingScheme.Exchange or RouterNamingScheme.OwnCode || (op.Schemes & c.Scheme) != 0).ToList();
    }

    // The operator's own site code, bare, numbered ("kanc", "kan1"), or ahead of Cox's router role
    // ("kscybbrj01"). True when the token was one.
    private static bool AddOwnCode(string raw, RouterOperator? op, List<RouterHostnameLocation> found)
    {
        if (op?.OwnCodes is not { } codes) return false;
        var core = raw.Trim(Digits);
        if (CoxRouter.Match(raw) is { Success: true } cox) core = cox.Groups[1].Value;
        else if (SiteRoleShape.Match(raw) is { Success: true } site) core = site.Groups[1].Value;
        if (!codes.TryGetValue(core, out var cityName)) return false;
        var numbered = raw.Length > core.Length && char.IsAsciiDigit(raw[^1]);
        found.AddRange(RouterLocationCities.All.Where(c => c.Name == cityName)
            .Select(c => new RouterHostnameLocation(c, raw,
                numbered ? RouterLocationMatch.NumberedCode : RouterLocationMatch.Code) { Scheme = RouterNamingScheme.OwnCode }));
        return true;
    }

    private static void AddMatches(string raw, string? qualifier, RouterOperator? op, List<RouterHostnameLocation> found)
    {
        if (ByName.TryGetValue(raw, out var named))
        {
            AddNamed(raw, named, qualifier, found);
            return;
        }

        // A code with its site number and a trailing letter or role: "tky001bb01" (IIJ), "chi01a" (Liberty Global).
        var site = SiteRoleShape.Match(raw);
        if (!site.Success) site = SiteLetterShape.Match(raw);
        if (site.Success && ByCode.TryGetValue(site.Groups[1].Value, out var siteCities))
        {
            found.AddRange(siteCities.Select(c => new RouterHostnameLocation(c, raw, RouterLocationMatch.NumberedCode)
                { Scheme = RouterNamingScheme.AirportCode }));
            return;
        }

        var token = raw.Trim(Digits);
        if (token.Length < MinCodeLength || !token.All(char.IsAsciiLetterLower)) return;
        var numbered = raw.Length > token.Length && char.IsAsciiDigit(raw[^1]);

        if (token.Length == 6 && ByClli.TryGetValue(token, out var clli))
        {
            found.AddRange(clli.Select(c => new RouterHostnameLocation(c, raw, RouterLocationMatch.Exact) { Scheme = RouterNamingScheme.Clli }));
            return;
        }
        if (token.Length == 4 && qualifier != null && ByClli.TryGetValue(token + qualifier, out var split))
        {
            found.AddRange(split.Select(c => new RouterHostnameLocation(c, raw, RouterLocationMatch.Exact) { Scheme = RouterNamingScheme.Clli }));
            return;
        }
        if (token.Length >= MinNameLength && ByName.TryGetValue(token, out var name))
        {
            AddNamed(raw, name, qualifier, found);
            return;
        }
        if (ByCode.TryGetValue(token, out var code))
        {
            AddCodes(raw, code, numbered, qualifier, found);
            return;
        }
        AddGenerated(raw, token, numbered, qualifier, op, found);
    }

    private static void AddCodes(string raw, IEnumerable<RouterCity> cities, bool numbered, string? qualifier, List<RouterHostnameLocation> found)
    {
        var match = numbered ? RouterLocationMatch.NumberedCode : RouterLocationMatch.Code;
        // A country or state label after the code that names another place rules the city out:
        // "rb-dca-3.za" is not Washington.
        found.AddRange(cities
            .Where(c => qualifier == null || !GeoQualifiers.Contains(qualifier) || InRegion(c, qualifier))
            .Select(c => new RouterHostnameLocation(c, raw, match) { Scheme = RouterNamingScheme.AirportCode }));
    }

    // The generated codes, read only when the curated list has no match for the token: a derived CLLI
    // code ("lncl01-ne"), or an airport. A medium airport needs an operator known to use airport codes,
    // and router words that are also airport codes are never read.
    private static void AddGenerated(string raw, string token, bool numbered, string? qualifier, RouterOperator? op, List<RouterHostnameLocation> found)
    {
        var clliKey = token.Length == 6 ? token : token.Length == 4 && qualifier != null ? token + qualifier : null;
        if (clliKey != null && RouterPlaceData.Clli.TryGetValue(clliKey, out var clliCity))
        {
            found.Add(new RouterHostnameLocation(clliCity, raw, RouterLocationMatch.Exact) { Scheme = RouterNamingScheme.Clli });
            return;
        }
        if (token.Length != 3 || RouterPlaceData.NotAirports.Contains(token)
            || !RouterPlaceData.Airports.TryGetValue(token, out var airport)) return;
        if (!airport.Large && (op == null || (op.Schemes & RouterNamingScheme.AirportCode) == 0)) return;
        AddCodes(raw, [airport.City], numbered, qualifier, found);
    }

    // The two-letter labels that name a place: every state and country the city list uses.
    private static readonly HashSet<string> GeoQualifiers = RouterLocationCities.All
        .SelectMany(c => new[] { c.Region, c.CountryCode })
        .Where(q => q.Length == 2)
        .Select(q => q.ToLowerInvariant())
        .ToHashSet();

    // An Equinix data center: the metro code and its number ("ch1").
    private static void AddEquinix(string part, List<RouterHostnameLocation> found)
    {
        if (part.Length < 3 || !char.IsAsciiLetterLower(part[0]) || !char.IsAsciiLetterLower(part[1])
            || !part[2..].All(char.IsAsciiDigit)) return;
        if (!RouterLocationCities.EquinixMetros.TryGetValue(part[..2], out var metro)) return;
        found.AddRange(RouterLocationCities.All.Where(c => c.Name == metro)
            .Select(c => new RouterHostnameLocation(c, part, RouterLocationMatch.Name)
                { IsExchange = true, Scheme = RouterNamingScheme.Exchange }));
    }

    // A region label after a name confirms it, and one that names a different region rules it out.
    private static void AddNamed(string raw, RouterCity[] cities, string? qualifier, List<RouterHostnameLocation> found)
    {
        if (qualifier == null)
        {
            found.AddRange(cities.Select(c => new RouterHostnameLocation(c, raw, RouterLocationMatch.Name) { Scheme = RouterNamingScheme.CityName }));
            return;
        }
        found.AddRange(cities
            .Where(c => InRegion(c, qualifier))
            .Select(c => new RouterHostnameLocation(c, raw, RouterLocationMatch.Exact) { Scheme = RouterNamingScheme.CityName }));
    }

    private static bool InRegion(RouterCity city, string qualifier) =>
        string.Equals(city.Region, qualifier, StringComparison.OrdinalIgnoreCase)
        || string.Equals(city.CountryCode, qualifier, StringComparison.OrdinalIgnoreCase);

    private static string? TwoLetters(string s) =>
        s.Length == 2 && char.IsAsciiLetterLower(s[0]) && char.IsAsciiLetterLower(s[1]) ? s : null;

    private static Dictionary<string, RouterCity[]> Index(Func<RouterCity, string[]> keys) =>
        RouterLocationCities.All
            .SelectMany(c => keys(c).Select(k => (Key: k.ToLowerInvariant(), City: c)))
            .GroupBy(x => x.Key)
            .ToDictionary(g => g.Key, g => g.Select(x => x.City).ToArray());
}
