using System.Net;
using MaxMind.GeoIP2;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Threats.Models;
using SharpCompress.Archives;
using SharpCompress.Archives.Tar;
using SharpCompress.Common;
using SharpCompress.Compressors;
using SharpCompress.Compressors.Deflate;

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
    /// For flow events where the source IP is internal (RFC1918), enriches on the destination IP
    /// instead, since the external endpoint is what needs geo data.
    /// </summary>
    public void EnrichEvents(List<ThreatEvent> events)
    {
        if (_cityReader == null && _asnReader == null)
            return;

        // Cache lookups per IP within the batch
        var cache = new Dictionary<string, GeoInfo>();

        foreach (var evt in events)
        {
            // For flow events with internal source, enrich on the destination IP
            var enrichIp = evt.SourceIp;
            if (evt.EventSource == EventSource.TrafficFlow &&
                !string.IsNullOrEmpty(evt.SourceIp) &&
                IPAddress.TryParse(evt.SourceIp, out var srcIp) &&
                IsPrivateOrReserved(srcIp) &&
                !string.IsNullOrEmpty(evt.DestIp))
            {
                enrichIp = evt.DestIp;
            }

            if (!cache.TryGetValue(enrichIp, out var geo))
            {
                geo = Enrich(enrichIp);
                cache[enrichIp] = geo;
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

    /// <summary>
    /// Download GeoLite2 databases from MaxMind using a license key.
    /// </summary>
    public async Task<(bool Success, string Message)> DownloadDatabasesAsync(
        string licenseKey, string dataPath, HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        var editions = new[] { "GeoLite2-City", "GeoLite2-ASN" };
        var errors = new List<string>();

        foreach (var edition in editions)
        {
            try
            {
                var url = $"https://download.maxmind.com/app/geoip_download?edition_id={edition}&license_key={licenseKey}&suffix=tar.gz";
                _logger.LogInformation("Downloading {Edition} database...", edition);

                using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    errors.Add($"{edition}: HTTP {(int)response.StatusCode} - {body}");
                    continue;
                }

                // Download to temp file, then extract
                var tempPath = Path.Combine(dataPath, $"{edition}.tar.gz");
                try
                {
                    await using (var fs = File.Create(tempPath))
                    {
                        await response.Content.CopyToAsync(fs, cancellationToken);
                    }

                    // Extract .mmdb from tar.gz
                    var targetFile = Path.Combine(dataPath, $"{edition}.mmdb");
                    var extracted = false;

                    await using var fileStream = File.OpenRead(tempPath);
                    await using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
                    using var archive = TarArchive.Open(gzipStream);

                    foreach (var entry in archive.Entries)
                    {
                        if (entry.Key != null && entry.Key.EndsWith(".mmdb", StringComparison.OrdinalIgnoreCase) && !entry.IsDirectory)
                        {
                            entry.WriteToFile(targetFile, new ExtractionOptions { Overwrite = true });
                            extracted = true;
                            _logger.LogInformation("Extracted {Edition}.mmdb ({Size:F1} MB)", edition, new FileInfo(targetFile).Length / 1_048_576.0);
                            break;
                        }
                    }

                    if (!extracted)
                    {
                        errors.Add($"{edition}: No .mmdb file found in archive");
                    }
                }
                finally
                {
                    // Clean up temp tar.gz
                    try { File.Delete(tempPath); } catch { /* ignore */ }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download {Edition}", edition);
                errors.Add($"{edition}: {ex.Message}");
            }
        }

        if (errors.Count == 0)
        {
            Reload(dataPath);
            return (true, "Both databases downloaded and loaded successfully.");
        }
        else if (errors.Count < editions.Length)
        {
            Reload(dataPath);
            return (true, $"Partial success. Errors: {string.Join("; ", errors)}");
        }

        return (false, $"Download failed: {string.Join("; ", errors)}");
    }

    /// <summary>
    /// Reload databases from disk (e.g., after download). Disposes existing readers and re-initializes.
    /// </summary>
    public void Reload(string dataPath)
    {
        lock (_initLock)
        {
            _cityReader?.Dispose();
            _asnReader?.Dispose();
            _cityReader = null;
            _asnReader = null;
            _initialized = false;
        }

        Initialize(dataPath);
    }

    public void Dispose()
    {
        _cityReader?.Dispose();
        _asnReader?.Dispose();
        GC.SuppressFinalize(this);
    }
}
