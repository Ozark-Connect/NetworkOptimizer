using System.Net;
using MaxMind.GeoIP2;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Threats.Models;

namespace NetworkOptimizer.Threats.Enrichment;

/// <summary>
/// Enriches threat events with geographic and ASN data using MaxMind GeoLite2 databases.
/// Thread-safe singleton - DatabaseReader is safe for concurrent reads.
/// </summary>
public class GeoEnrichmentService : IDisposable
{
    private readonly ILogger<GeoEnrichmentService> _logger;
    private DatabaseReader? _cityReader;
    private DatabaseReader? _asnReader;
    private bool _initialized;
    private readonly object _initLock = new();

    public bool IsCityAvailable => _cityReader != null;
    public bool IsAsnAvailable => _asnReader != null;

    public GeoEnrichmentService(ILogger<GeoEnrichmentService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Initialize readers from the data directory. Call once on startup.
    /// </summary>
    public void Initialize(string dataPath)
    {
        lock (_initLock)
        {
            if (_initialized) return;
            _initialized = true;

            var cityPath = Path.Combine(dataPath, "GeoLite2-City.mmdb");
            var asnPath = Path.Combine(dataPath, "GeoLite2-ASN.mmdb");

            if (File.Exists(cityPath))
            {
                try
                {
                    _cityReader = new DatabaseReader(cityPath);
                    _logger.LogInformation("Loaded GeoLite2-City database from {Path}", cityPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load GeoLite2-City database");
                }
            }
            else
            {
                _logger.LogWarning("GeoLite2-City.mmdb not found at {Path}. Geo enrichment will be unavailable. " +
                    "Download from https://dev.maxmind.com/geoip/geolite2-free-geolocation-data", cityPath);
            }

            if (File.Exists(asnPath))
            {
                try
                {
                    _asnReader = new DatabaseReader(asnPath);
                    _logger.LogInformation("Loaded GeoLite2-ASN database from {Path}", asnPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load GeoLite2-ASN database");
                }
            }
            else
            {
                _logger.LogWarning("GeoLite2-ASN.mmdb not found at {Path}. ASN enrichment will be unavailable", asnPath);
            }
        }
    }

    /// <summary>
    /// Enrich a single IP address with geo/ASN data.
    /// </summary>
    public GeoInfo Enrich(string ipAddress)
    {
        if (string.IsNullOrEmpty(ipAddress) || !IPAddress.TryParse(ipAddress, out var ip))
            return GeoInfo.Empty;

        // Skip private/reserved ranges
        if (IsPrivateOrReserved(ip))
            return GeoInfo.Empty;

        string? countryCode = null;
        string? city = null;
        double? lat = null;
        double? lon = null;
        int? asn = null;
        string? asnOrg = null;

        if (_cityReader != null)
        {
            try
            {
                if (_cityReader.TryCity(ip, out var cityResult))
                {
                    countryCode = cityResult?.Country?.IsoCode;
                    city = cityResult?.City?.Name;
                    lat = cityResult?.Location?.Latitude;
                    lon = cityResult?.Location?.Longitude;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "GeoLite2 city lookup failed for {Ip}", ipAddress);
            }
        }

        if (_asnReader != null)
        {
            try
            {
                if (_asnReader.TryAsn(ip, out var asnResult))
                {
                    asn = (int?)asnResult?.AutonomousSystemNumber;
                    asnOrg = asnResult?.AutonomousSystemOrganization;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "GeoLite2 ASN lookup failed for {Ip}", ipAddress);
            }
        }

        return new GeoInfo
        {
            CountryCode = countryCode,
            City = city,
            Latitude = lat,
            Longitude = lon,
            Asn = asn,
            AsnOrg = asnOrg
        };
    }

    /// <summary>
    /// Batch-enrich threat events with geo/ASN data.
    /// </summary>
    public void EnrichEvents(List<ThreatEvent> events)
    {
        if (_cityReader == null && _asnReader == null)
            return;

        // Cache lookups per IP within the batch
        var cache = new Dictionary<string, GeoInfo>();

        foreach (var evt in events)
        {
            if (!cache.TryGetValue(evt.SourceIp, out var geo))
            {
                geo = Enrich(evt.SourceIp);
                cache[evt.SourceIp] = geo;
            }

            evt.CountryCode = geo.CountryCode;
            evt.City = geo.City;
            evt.Latitude = geo.Latitude;
            evt.Longitude = geo.Longitude;
            evt.Asn = geo.Asn;
            evt.AsnOrg = geo.AsnOrg;
        }
    }

    private static bool IsPrivateOrReserved(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();

        // IPv4
        if (bytes.Length == 4)
        {
            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // 127.0.0.0/8 (loopback)
            if (bytes[0] == 127) return true;
            // 169.254.0.0/16 (link-local)
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            // 0.0.0.0/8
            if (bytes[0] == 0) return true;
            // 224.0.0.0/4 (multicast)
            if (bytes[0] >= 224) return true;
        }

        // IPv6 loopback and link-local
        if (ip.IsIPv6LinkLocal || IPAddress.IsLoopback(ip))
            return true;

        return false;
    }

    /// <summary>
    /// Get file info for the GeoLite2 databases.
    /// </summary>
    public (bool CityExists, DateTime? CityDate, bool AsnExists, DateTime? AsnDate) GetDatabaseInfo(string dataPath)
    {
        var cityPath = Path.Combine(dataPath, "GeoLite2-City.mmdb");
        var asnPath = Path.Combine(dataPath, "GeoLite2-ASN.mmdb");

        return (
            File.Exists(cityPath),
            File.Exists(cityPath) ? File.GetLastWriteTimeUtc(cityPath) : null,
            File.Exists(asnPath),
            File.Exists(asnPath) ? File.GetLastWriteTimeUtc(asnPath) : null
        );
    }

    public void Dispose()
    {
        _cityReader?.Dispose();
        _asnReader?.Dispose();
        GC.SuppressFinalize(this);
    }
}
