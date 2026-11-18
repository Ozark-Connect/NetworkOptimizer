namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// A city a backbone router can sit in, with the tokens operators put in router hostnames for it.
/// </summary>
/// <param name="Name">The city's English name.</param>
/// <param name="Region">State or province code (US, Canada, Australia); empty elsewhere.</param>
/// <param name="CountryCode">ISO 3166-1 alpha-2 country code.</param>
/// <param name="Latitude">City center latitude, in degrees.</param>
/// <param name="Longitude">City center longitude, in degrees.</param>
/// <param name="Codes">Short codes: IATA airport codes and the operator codes in common use ("ash", "kanc").</param>
/// <param name="Clli">Six-letter CLLI place codes: four for the city, two for the state or country ("dllstx").</param>
/// <param name="Names">The city's name run together ("losangeles"), plus facility names that stand for it ("champa").</param>
public sealed record RouterCity(
    string Name,
    string Region,
    string CountryCode,
    double Latitude,
    double Longitude,
    string[] Codes,
    string[] Clli,
    string[] Names);

/// <summary>How an operator writes a router's location into its name.</summary>
[Flags]
public enum RouterNamingScheme
{
    /// <summary>No scheme.</summary>
    None = 0,

    /// <summary>An airport code, usually with a site number ("lax01", "dfw7").</summary>
    AirportCode = 1,

    /// <summary>A six-letter CLLI place code ("dllstx14", "dlls02-tx").</summary>
    Clli = 2,

    /// <summary>The city's name or a facility that stands for it ("dallas2", "350ecermak").</summary>
    CityName = 4,

    /// <summary>The operator's own site codes ("kanc", "kscy").</summary>
    OwnCode = 8,

    /// <summary>An internet exchange's name (an Equinix data center, an exchange's own domain).</summary>
    Exchange = 16,
}

/// <summary>A network operator, as its router names identify it.</summary>
/// <param name="Name">The operator's name.</param>
/// <param name="Asn">Its main autonomous system number.</param>
/// <param name="Domains">The domains it names routers in.</param>
/// <param name="Schemes">How it writes a location; exchange names are read for every operator.</param>
/// <param name="OwnCodes">Its own site codes, by city name.</param>
/// <param name="NotPlaces">Tokens of its that look like codes but name no place (Akamai's network tags).</param>
public sealed record RouterOperator(
    string Name,
    int Asn,
    string[] Domains,
    RouterNamingScheme Schemes,
    IReadOnlyDictionary<string, string>? OwnCodes = null,
    string[]? NotPlaces = null);

/// <summary>
/// The curated cities <see cref="RouterHostnameLocator"/> reads first: backbone and peering hubs, with
/// their operator codes, facility names, and irregular CLLI codes. It is the only list hub snapping
/// uses. Every other airport and CLLI code comes from <see cref="RouterPlaceData"/>, read only when
/// this list has no match, with router words that are also airport codes ("man", "sat") left out.
/// </summary>
public static class RouterLocationCities
{
    /// <summary>Every city, in no particular order.</summary>
    public static readonly IReadOnlyList<RouterCity> All =
    [
        // United States
        new("Dallas", "TX", "US", 32.78, -96.80, ["dfw", "dal"], ["dllstx"], ["dallas", "1950stemmons"]),
        new("Houston", "TX", "US", 29.76, -95.37, ["hou", "iah"], ["hstntx"], ["houston"]),
        new("Austin", "TX", "US", 30.27, -97.74, ["aus"], ["austtx"], ["austin"]),
        new("San Antonio", "TX", "US", 29.42, -98.49, [], ["snantx"], ["sanantonio"]),
        new("El Paso", "TX", "US", 31.76, -106.49, ["elp"], ["elpstx"], ["elpaso"]),
        new("Atlanta", "GA", "US", 33.75, -84.39, ["atl"], ["atlnga"], ["atlanta", "56marietta", "doraville"]),
        new("Tucker", "GA", "US", 33.85, -84.22, [], ["tukrga"], ["tucker"]),
        new("Miami", "FL", "US", 25.76, -80.19, ["mia"], ["miamfl"], ["miami"]),
        new("Tampa", "FL", "US", 27.95, -82.46, ["tpa"], ["tampfl"], ["tampa"]),
        new("Jacksonville", "FL", "US", 30.33, -81.66, ["jax"], ["jcvlfl"], ["jacksonville"]),
        new("Orlando", "FL", "US", 28.54, -81.38, ["mco"], ["orldfl"], ["orlando"]),
        new("Charlotte", "NC", "US", 35.23, -80.84, ["clt"], ["chrlnc"], ["charlotte"]),
        new("Raleigh", "NC", "US", 35.78, -78.64, ["rdu"], ["rlghnc"], ["raleigh"]),
        new("Nashville", "TN", "US", 36.16, -86.78, ["bna"], ["nsvltn"], ["nashville"]),
        new("Memphis", "TN", "US", 35.15, -90.05, ["mem"], ["mmphtn"], ["memphis"]),
        new("Jackson", "MS", "US", 32.30, -90.18, ["jan"], ["jcsnms"], []),
        new("Little Rock", "AR", "US", 34.75, -92.29, ["lit"], ["ltrkar"], ["littlerock"]),
        new("New Orleans", "LA", "US", 29.95, -90.07, ["msy"], ["nworla"], ["neworleans"]),
        new("Kansas City", "MO", "US", 39.10, -94.58, ["mci", "kanc", "kcm"], ["kscymo"], ["kansascity"]),
        new("St. Louis", "MO", "US", 38.63, -90.20, ["stl"], ["stlsmo"], ["stlouis", "saintlouis"]),
        new("Chicago", "IL", "US", 41.88, -87.63, ["ord", "chi", "mdw"], ["chcgil"], ["chicago", "350ecermak", "northlake"]),
        new("Minneapolis", "MN", "US", 44.98, -93.27, ["msp"], ["mplsmn"], ["minneapolis"]),
        new("Detroit", "MI", "US", 42.33, -83.05, ["dtw"], ["dtrtmi"], ["detroit"]),
        new("Cleveland", "OH", "US", 41.50, -81.69, ["cle"], ["clevoh"], ["cleveland"]),
        new("Columbus", "OH", "US", 39.96, -83.00, ["cmh"], ["clmboh"], ["columbus"]),
        new("Indianapolis", "IN", "US", 39.77, -86.16, ["ind"], ["ipltin"], ["indianapolis"]),
        new("Denver", "CO", "US", 39.74, -104.99, ["den", "dvr"], ["dnvrco"], ["denver", "champa", "1601milehigh"]),
        new("Salt Lake City", "UT", "US", 40.76, -111.89, ["slc"], ["slkcut"], ["saltlakecity"]),
        new("Ogden", "UT", "US", 41.22, -111.97, ["ogd"], ["ogdnut"], ["ogden"]),
        new("Phoenix", "AZ", "US", 33.45, -112.07, ["phx"], ["phnxaz"], ["phoenix"]),
        new("Las Vegas", "NV", "US", 36.17, -115.14, ["las"], ["lsvgnv"], ["lasvegas"]),
        new("Los Angeles", "CA", "US", 34.05, -118.24, ["lax"], ["lsanca"], ["losangeles"]),
        new("San Diego", "CA", "US", 32.72, -117.16, [], ["sndgca"], ["sandiego"]),
        new("San Jose", "CA", "US", 37.34, -121.89, ["sjc"], ["snjsca"], ["sanjose"]),
        new("San Francisco", "CA", "US", 37.77, -122.42, ["sfo"], ["snfcca"], ["sanfrancisco"]),
        new("Palo Alto", "CA", "US", 37.44, -122.14, ["pao"], ["plalca"], ["paloalto"]),
        new("Fremont", "CA", "US", 37.55, -121.99, ["fmt"], ["frmnca"], ["fremont"]),
        new("Sacramento", "CA", "US", 38.58, -121.49, ["smf"], ["scrmca"], ["sacramento"]),
        new("Seattle", "WA", "US", 47.61, -122.33, ["sea"], ["sttlwa"], ["seattle"]),
        new("Portland", "OR", "US", 45.52, -122.68, ["pdx"], ["ptldor"], ["portland"]),
        new("Boise", "ID", "US", 43.62, -116.21, ["boi"], ["boisid"], ["boise"]),
        new("Ashburn", "VA", "US", 39.04, -77.49, ["iad", "ash"], ["asbnva"], ["ashburn"]),
        new("Reston", "VA", "US", 38.96, -77.36, [], ["rstnva"], ["reston"]),
        new("Washington", "DC", "US", 38.90, -77.04, ["dca", "wdc", "was"], ["wshndc"], ["washington"]),
        new("New York", "NY", "US", 40.71, -74.01, ["jfk", "lga", "nyc"], ["nycmny"], ["newyork"]),
        new("Newark", "NJ", "US", 40.74, -74.17, ["ewr"], ["nwrknj"], ["newark"]),
        new("Philadelphia", "PA", "US", 39.95, -75.17, ["phl"], ["phlapa"], ["philadelphia"]),
        new("Pittsburgh", "PA", "US", 40.44, -80.00, ["pit"], ["pitbpa"], ["pittsburgh"]),
        new("Boston", "MA", "US", 42.36, -71.06, ["bos"], ["bstnma"], ["boston"]),
        new("Omaha", "NE", "US", 41.26, -95.94, ["oma"], ["omahne"], ["omaha"]),
        new("Oklahoma City", "OK", "US", 35.47, -97.52, ["okc"], ["okcyok"], ["oklahomacity"]),
        new("Tulsa", "OK", "US", 36.15, -95.99, ["tul"], ["tulsok"], ["tulsa"]),
        new("Albuquerque", "NM", "US", 35.08, -106.65, ["abq"], ["albqnm"], ["albuquerque"]),
        new("Honolulu", "HI", "US", 21.31, -157.86, ["hnl"], [], ["honolulu"]),

        // Canada and Mexico
        new("Toronto", "ON", "CA", 43.65, -79.38, ["yyz"], [], ["toronto"]),
        new("Montreal", "QC", "CA", 45.50, -73.57, ["yul", "mtl"], [], ["montreal"]),
        new("Vancouver", "BC", "CA", 49.28, -123.12, ["yvr"], [], ["vancouver"]),
        new("Mexico City", "", "MX", 19.43, -99.13, ["mex"], [], ["mexicocity"]),
        new("Queretaro", "", "MX", 20.59, -100.39, ["qro"], [], ["queretaro"]),

        // South America
        new("Sao Paulo", "", "BR", -23.55, -46.63, ["gru", "sao"], [], ["saopaulo"]),
        new("Rio de Janeiro", "", "BR", -22.91, -43.17, ["gig", "rio"], [], ["riodejaneiro"]),
        new("Santiago", "", "CL", -33.45, -70.67, ["scl"], [], ["santiago"]),
        new("Buenos Aires", "", "AR", -34.60, -58.38, ["eze"], [], ["buenosaires"]),
        new("Bogota", "", "CO", 4.71, -74.07, ["bog"], [], ["bogota"]),
        new("Lima", "", "PE", -12.05, -77.04, ["lim"], [], ["lima"]),

        // Europe
        new("London", "", "GB", 51.51, -0.13, ["lhr", "lon", "ldn"], ["londen"], ["london"]),
        new("Slough", "", "GB", 51.51, -0.59, ["slou"], [], ["slough"]),
        new("Manchester", "", "GB", 53.48, -2.24, [], [], ["manchester"]),
        new("Amsterdam", "", "NL", 52.37, 4.90, ["ams"], ["amstnl"], ["amsterdam"]),
        new("Haarlem", "", "NL", 52.38, 4.64, [], [], ["haarlem"]),
        new("Frankfurt", "", "DE", 50.11, 8.68, ["fra", "ffm"], ["frnkge"], ["frankfurt"]),
        new("Paris", "", "FR", 48.86, 2.35, ["cdg", "par", "prs"], ["parsfr"], ["paris"]),
        new("Marseille", "", "FR", 43.30, 5.37, ["mrs"], [], ["marseille"]),
        new("Brussels", "", "BE", 50.85, 4.35, ["bru"], [], ["brussels"]),
        new("Madrid", "", "ES", 40.42, -3.70, ["mad"], ["mdrdsp"], ["madrid"]),
        new("Lisbon", "", "PT", 38.72, -9.14, ["lis"], [], ["lisbon"]),
        new("Milan", "", "IT", 45.46, 9.19, ["mxp"], [], ["milan"]),
        new("Zurich", "", "CH", 47.38, 8.54, ["zrh"], [], ["zurich"]),
        new("Vienna", "", "AT", 48.21, 16.37, ["vie"], [], ["vienna"]),
        new("Stockholm", "", "SE", 59.33, 18.07, ["arn", "sto"], [], ["stockholm"]),
        new("Copenhagen", "", "DK", 55.68, 12.57, ["cph"], [], ["copenhagen"]),
        new("Oslo", "", "NO", 59.91, 10.75, ["osl"], [], ["oslo"]),
        new("Warsaw", "", "PL", 52.23, 21.01, ["waw"], [], ["warsaw"]),
        new("Dublin", "", "IE", 53.35, -6.26, ["dub"], [], ["dublin"]),

        // Asia, Pacific, Africa, Middle East
        new("Tokyo", "", "JP", 35.68, 139.69, ["nrt", "hnd", "tyo"], ["tokyjp"], ["tokyo"]),
        new("Osaka", "", "JP", 34.69, 135.50, ["kix", "osa"], ["osakjp"], ["osaka"]),
        new("Seoul", "", "KR", 37.57, 126.98, ["icn", "sel"], [], ["seoul"]),
        new("Hong Kong", "", "HK", 22.32, 114.17, ["hkg"], [], ["hongkong"]),
        new("Taipei", "", "TW", 25.03, 121.57, ["tpe"], [], ["taipei"]),
        new("Singapore", "", "SG", 1.35, 103.82, ["sin"], ["sngpsi"], ["singapore"]),
        new("Manila", "", "PH", 14.60, 120.98, ["mnl"], [], ["manila"]),
        new("Guam", "", "GU", 13.44, 144.79, ["gum"], [], ["guam"]),
        new("Sydney", "NSW", "AU", -33.87, 151.21, ["syd"], [], ["sydney"]),
        new("Melbourne", "VIC", "AU", -37.81, 144.96, ["mel"], [], ["melbourne"]),
        new("Auckland", "", "NZ", -36.85, 174.76, ["akl"], [], ["auckland"]),
        new("Mumbai", "", "IN", 19.08, 72.88, ["bom"], [], ["mumbai"]),
        new("Chennai", "", "IN", 13.08, 80.27, ["maa"], [], ["chennai"]),
        new("Dubai", "", "AE", 25.20, 55.27, ["dxb"], [], ["dubai"]),
        new("Santa Clara", "CA", "US", 37.35, -121.96, [], [], ["santaclara"]),
        new("Terre Haute", "IN", "US", 39.47, -87.41, ["huf"], [], ["terrehaute"]),
        new("Norfolk", "VA", "US", 36.85, -76.29, ["orf"], [], ["norfolk"]),
        new("Lille", "", "FR", 50.63, 3.06, ["lil"], [], ["lille"]),
        new("Bratislava", "", "SK", 48.15, 17.11, ["bts"], [], ["bratislava"]),
        new("Curitiba", "", "BR", -25.43, -49.27, ["cwb"], [], ["curitiba"]),
        new("Cape Town", "", "ZA", -33.92, 18.42, ["cpt"], [], ["capetown"]),
        new("Hamburg", "", "DE", 53.55, 9.99, [], [], ["hamburg"]),
        new("Bilbao", "", "ES", 43.26, -2.93, ["bio"], [], ["bilbao"]),
        new("Johannesburg", "", "ZA", -26.20, 28.05, ["jnb"], [], ["johannesburg"]),
    ];

    /// <summary>
    /// Internet exchanges in one city whose own domain is the only location in a member's hostname
    /// ("cloudflare.slix.net"), by that domain.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ExchangeDomains = new Dictionary<string, string>
    {
        ["slix.net"] = "Salt Lake City",
    };

    /// <summary>
    /// Operators whose router names were read from traces, by the domain they name routers in: the
    /// schemes each one writes, its own site codes, and tokens of its that are not places. A known
    /// operator's names are read with its schemes alone, so its own code means what it means there
    /// ("san" is San Antonio to Arelion, where an airport code would read San Diego).
    /// </summary>
    public static readonly IReadOnlyList<RouterOperator> Operators =
    [
        new("Arelion", 1299, ["twelve99.net", "twelve99-cust.net"], RouterNamingScheme.OwnCode, new Dictionary<string, string>
        {
            ["adm"] = "Amsterdam", ["ash"] = "Ashburn", ["atl"] = "Atlanta", ["chi"] = "Chicago", ["den"] = "Denver",
            ["dls"] = "Dallas", ["ewr"] = "Newark", ["hbg"] = "Hamburg", ["hnk"] = "Hong Kong", ["ffm"] = "Frankfurt", ["hou"] = "Houston", ["kanc"] = "Kansas City",
            ["kbn"] = "Copenhagen", ["lax"] = "Los Angeles", ["ldn"] = "London", ["nash"] = "Nashville",
            ["nyk"] = "New York", ["omha"] = "Omaha", ["palo"] = "Palo Alto", ["prs"] = "Paris", ["rest"] = "Reston",
            ["san"] = "San Antonio", ["sea"] = "Seattle", ["slou"] = "Slough", ["stlo"] = "St. Louis", ["sto"] = "Stockholm",
        }),
        new("NTT", 2914, ["ntt.net"], RouterNamingScheme.Clli),
        new("Lumen", 3356, ["lumen.tech", "level3.net"], RouterNamingScheme.CityName | RouterNamingScheme.AirportCode,
            new Dictionary<string, string> { ["kac"] = "Kansas City" }),
        new("Cogent", 174, ["cogentco.com"], RouterNamingScheme.AirportCode),
        new("GTT", 3257, ["gtt.net"], RouterNamingScheme.AirportCode,
            new Dictionary<string, string> { ["kan"] = "Kansas City" }),
        new("Tata", 6453, ["as6453.net"], RouterNamingScheme.CityName),
        new("Hurricane Electric", 6939, ["he.net"], RouterNamingScheme.AirportCode | RouterNamingScheme.CityName),
        new("Zayo", 6461, ["zayo.com", "above.net"], RouterNamingScheme.AirportCode),
        new("Verizon", 701, ["alter.net"], RouterNamingScheme.AirportCode),
        new("Comcast", 7922, ["comcast.net"], RouterNamingScheme.CityName),
        new("Cox", 22773, ["cox.net"], RouterNamingScheme.OwnCode, new Dictionary<string, string>
        {
            ["ashb"] = "Ashburn", ["chgo"] = "Chicago", ["cled"] = "Cleveland", ["dals"] = "Dallas",
            ["dlls"] = "Dallas", ["kscy"] = "Kansas City", ["lang"] = "Los Angeles", ["nyrk"] = "New York",
            ["palt"] = "Palo Alto", ["sanj"] = "San Jose",
        }),
        new("Uniti", 7029, ["uniti.net"], RouterNamingScheme.Clli),
        new("Liberty Global", 6830, ["aorta.net"], RouterNamingScheme.AirportCode),
        new("Akamai", 20940, ["akamai.com"], RouterNamingScheme.AirportCode,
            NotPlaces: ["icn", "ien", "mag", "fab", "sdn"]),
        new("Linode", 63949, ["linode.com"], RouterNamingScheme.AirportCode,
            new Dictionary<string, string> { ["rin"] = "Dallas" }),
        new("Microsoft", 8075, ["msn.net"], RouterNamingScheme.AirportCode),
        new("Meta", 32934, ["tfbnw.net"], RouterNamingScheme.AirportCode),
        new("IIJ", 2497, ["iij.net"], RouterNamingScheme.OwnCode,
            new Dictionary<string, string> { ["tky"] = "Tokyo", ["osk"] = "Osaka" }),
    ];

    private static readonly Dictionary<string, RouterOperator> OperatorsByDomain = Operators
        .SelectMany(o => o.Domains.Select(d => (Domain: d, Operator: o)))
        .ToDictionary(x => x.Domain, x => x.Operator, StringComparer.OrdinalIgnoreCase);

    /// <summary>The operator that names routers in a domain ("twelve99.net"), or null when it is not a known one.</summary>
    public static RouterOperator? OperatorFor(string domain) => OperatorsByDomain.GetValueOrDefault(domain);

    /// <summary>
    /// Equinix metro codes, as members write a data center into a hostname ("eqix-ch1", "eqix-da3"),
    /// by the city each metro's data centers cluster around.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> EquinixMetros = new Dictionary<string, string>
    {
        ["at"] = "Atlanta", ["ch"] = "Chicago", ["da"] = "Dallas", ["dc"] = "Ashburn", ["la"] = "Los Angeles",
        ["mi"] = "Miami", ["ny"] = "New York", ["se"] = "Seattle", ["sv"] = "San Jose",
        ["am"] = "Amsterdam", ["fr"] = "Frankfurt", ["ld"] = "London", ["pa"] = "Paris",
        ["os"] = "Osaka", ["sg"] = "Singapore", ["sy"] = "Sydney", ["ty"] = "Tokyo",
    };
}
