using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NetworkOptimizer.Audit.Services;

/// <summary>
/// Downloads and indexes the IEEE OUI database for MAC vendor lookup.
/// The database is downloaded on startup and cached in memory.
/// </summary>
public class IeeeOuiDatabase : IIeeeOuiDatabase
{
    private const string IeeeOuiUrl = "https://standards-oui.ieee.org/oui/oui.txt";
    private const string CacheFileName = "ieee-oui-cache.txt";
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromDays(7);

    private readonly ILogger<IeeeOuiDatabase> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _cacheDirectory;
    private readonly ConcurrentDictionary<string, string> _ouiToVendor = new(StringComparer.OrdinalIgnoreCase);
    private bool _isLoaded;

    public IeeeOuiDatabase(ILogger<IeeeOuiDatabase> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;

        // Use app data directory for cache
        var isDocker = string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        _cacheDirectory = isDocker
            ? "/app/data"
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NetworkOptimizer");

        Directory.CreateDirectory(_cacheDirectory);
    }

    /// <summary>
    /// Number of OUI entries loaded
    /// </summary>
    public int Count => _ouiToVendor.Count;

    /// <summary>
    /// Whether the database has been loaded
    /// </summary>
    public bool IsLoaded => _isLoaded;

    /// <summary>
    /// Initialize the database - download from IEEE or load from cache
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isLoaded)
            return;

        var cachePath = Path.Combine(_cacheDirectory, CacheFileName);

        // Try to load from cache first
        if (await TryLoadFromCacheAsync(cachePath))
        {
            _isLoaded = true;
            return;
        }

        // Download from IEEE
        if (await TryDownloadAndCacheAsync(cachePath, cancellationToken))
        {
            _isLoaded = true;
            return;
        }

        // If all else fails, we'll just have an empty database
        // The curated OUI mappings will still work
        _logger.LogWarning("IEEE OUI database could not be loaded - vendor lookups will be limited");
        _isLoaded = true;
    }

    /// <summary>
    /// Look up vendor name by MAC address or OUI prefix
    /// </summary>
    /// <param name="macOrOui">Full MAC address or OUI prefix (e.g., "F8:01:B4" or "F8-01-B4-7B-4E-1B")</param>
    /// <returns>Vendor name or null if not found</returns>
    public string? GetVendor(string macOrOui)
    {
        if (string.IsNullOrEmpty(macOrOui))
            return null;

        // Before the OUI fold: these are matched on the WHOLE address, not a prefix.
        if (KnownFixedMacs.TryGetValue(NormalizeMac(macOrOui), out var fixedVendor))
            return fixedVendor;

        var oui = NormalizeToOui(macOrOui);
        return _ouiToVendor.TryGetValue(oui, out var vendor) ? vendor : null;
    }

    /// <summary>
    /// Vendors for specific locally-administered MACs. Such an address is by definition absent
    /// from the IEEE registry, so no amount of registry data will ever name it - but kit that
    /// ships with a FIXED one is still identifiable by the whole address. Every Starlink dish of
    /// this generation presents 26:12:AC:1A:80:01 on its LAN side, so a gateway neighbor lookup
    /// that came back blank was a name we already had and were not using. Keyed on the full MAC
    /// deliberately: the prefix is locally administered and says nothing on its own.
    /// </summary>
    private static readonly Dictionary<string, string> KnownFixedMacs =
        new(StringComparer.OrdinalIgnoreCase) { ["2612AC1A8001"] = "Starlink" };

    private static string NormalizeMac(string input) => input
        .Replace(":", "")
        .Replace("-", "")
        .Replace(".", "")
        .ToUpperInvariant();

    /// <summary>
    /// Check if a vendor exists in the database
    /// </summary>
    public bool HasVendor(string macOrOui)
    {
        return GetVendor(macOrOui) != null;
    }

    private async Task<bool> TryLoadFromCacheAsync(string cachePath)
    {
        try
        {
            if (!File.Exists(cachePath))
                return false;

            var fileInfo = new FileInfo(cachePath);
            if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc > CacheMaxAge)
            {
                _logger.LogInformation("IEEE OUI cache is stale ({Age} days old), will refresh",
                    (DateTime.UtcNow - fileInfo.LastWriteTimeUtc).TotalDays.ToString("F1"));
                return false;
            }

            var content = await File.ReadAllTextAsync(cachePath);
            var count = ParseOuiData(content);

            _logger.LogInformation("Loaded {Count} OUI entries from cache", count);
            return count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load IEEE OUI cache from {Path}", cachePath);
            return false;
        }
    }

    private async Task<bool> TryDownloadAndCacheAsync(string cachePath, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Downloading IEEE OUI database from {Url}", IeeeOuiUrl);

            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(30);

            var content = await httpClient.GetStringAsync(IeeeOuiUrl, cancellationToken);

            var count = ParseOuiData(content);
            _logger.LogInformation("Downloaded and parsed {Count} OUI entries from IEEE", count);

            if (count > 0)
            {
                // Cache for next time
                try
                {
                    await File.WriteAllTextAsync(cachePath, content, cancellationToken);
                    _logger.LogDebug("Cached IEEE OUI database to {Path}", cachePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cache IEEE OUI database");
                }
            }

            return count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download IEEE OUI database");
            return false;
        }
    }

    /// <summary>
    /// Parse IEEE OUI text format.
    /// Lines look like: "F8-01-B4   (hex)		LG Electronics (Mobile Communications)"
    /// </summary>
    private int ParseOuiData(string content)
    {
        _ouiToVendor.Clear();

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var count = 0;

        foreach (var line in lines)
        {
            // Look for lines with "(hex)" which contain the OUI mapping
            // Format: "XX-XX-XX   (hex)		Vendor Name"
            var hexIndex = line.IndexOf("(hex)", StringComparison.OrdinalIgnoreCase);
            if (hexIndex < 0)
                continue;

            // Extract OUI (before "(hex)")
            var ouiPart = line[..hexIndex].Trim();
            if (ouiPart.Length < 8) // "XX-XX-XX" = 8 chars
                continue;

            // Extract vendor name (after "(hex)")
            var vendorPart = line[(hexIndex + 5)..].Trim();
            if (string.IsNullOrEmpty(vendorPart))
                continue;

            // Normalize OUI to our format (XX:XX:XX)
            var oui = NormalizeToOui(ouiPart);
            if (oui.Length == 8) // "XX:XX:XX"
            {
                _ouiToVendor[oui] = vendorPart;
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Normalize MAC or OUI to standard format (XX:XX:XX)
    /// </summary>
    private static string NormalizeToOui(string input)
    {
        // Remove common separators and take first 6 characters
        var cleaned = input
            .Replace(":", "")
            .Replace("-", "")
            .Replace(".", "")
            .ToUpperInvariant();

        if (cleaned.Length >= 6)
        {
            return $"{cleaned[0..2]}:{cleaned[2..4]}:{cleaned[4..6]}";
        }

        return input.ToUpperInvariant();
    }
}
