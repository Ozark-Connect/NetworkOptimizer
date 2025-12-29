using Microsoft.Extensions.Logging;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Caches and provides access to the UniFi fingerprint database.
/// The database maps device IDs to names and categories.
/// </summary>
public class FingerprintDatabaseService
{
    private readonly ILogger<FingerprintDatabaseService> _logger;
    private readonly UniFiConnectionService _connectionService;

    private UniFiFingerprintDatabase? _database;
    private DateTime? _lastFetchTime;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);

    public FingerprintDatabaseService(
        ILogger<FingerprintDatabaseService> logger,
        UniFiConnectionService connectionService)
    {
        _logger = logger;
        _connectionService = connectionService;
    }

    /// <summary>
    /// Get the cached fingerprint database, fetching if needed
    /// </summary>
    public async Task<UniFiFingerprintDatabase?> GetDatabaseAsync(CancellationToken cancellationToken = default)
    {
        // Return cached if still valid
        if (_database != null && _lastFetchTime.HasValue &&
            DateTime.UtcNow - _lastFetchTime.Value < CacheDuration)
        {
            return _database;
        }

        // Fetch with lock to prevent concurrent fetches
        await _fetchLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring lock
            if (_database != null && _lastFetchTime.HasValue &&
                DateTime.UtcNow - _lastFetchTime.Value < CacheDuration)
            {
                return _database;
            }

            await FetchDatabaseAsync(cancellationToken);
            return _database;
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    /// <summary>
    /// Force refresh the fingerprint database
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _fetchLock.WaitAsync(cancellationToken);
        try
        {
            await FetchDatabaseAsync(cancellationToken);
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    private async Task FetchDatabaseAsync(CancellationToken cancellationToken)
    {
        if (!_connectionService.IsConnected || _connectionService.Client == null)
        {
            _logger.LogWarning("Cannot fetch fingerprint database: not connected to UniFi controller");
            return;
        }

        try
        {
            _logger.LogInformation("Fetching fingerprint database from UniFi controller...");

            _database = await _connectionService.Client.GetCompleteFingerprintDatabaseAsync(cancellationToken);
            _lastFetchTime = DateTime.UtcNow;

            if (_database != null)
            {
                _logger.LogInformation(
                    "Fingerprint database loaded: {DevTypes} device types, {Vendors} vendors, {Devices} specific devices",
                    _database.DevTypeIds.Count,
                    _database.VendorIds.Count,
                    _database.DevIds.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch fingerprint database");
        }
    }

    /// <summary>
    /// Look up a device name by its device ID (used for dev_id_override)
    /// </summary>
    public string? GetDeviceName(int? deviceId)
    {
        if (deviceId == null || _database == null)
            return null;

        if (_database.DevIds.TryGetValue(deviceId.Value.ToString(), out var entry))
        {
            return entry.Name?.Trim();
        }

        return null;
    }

    /// <summary>
    /// Look up device type name by ID (used for dev_cat)
    /// </summary>
    public string? GetDeviceTypeName(int? devTypeId) =>
        _database?.GetDeviceTypeName(devTypeId);

    /// <summary>
    /// Look up vendor name by ID
    /// </summary>
    public string? GetVendorName(int? vendorId) =>
        _database?.GetVendorName(vendorId);

    /// <summary>
    /// Get the device type ID for a specific device (from dev_ids lookup)
    /// </summary>
    public int? GetDeviceTypeId(int? deviceId)
    {
        if (deviceId == null || _database == null)
            return null;

        if (_database.DevIds.TryGetValue(deviceId.Value.ToString(), out var entry) &&
            int.TryParse(entry.DevTypeId, out var typeId))
        {
            return typeId;
        }

        return null;
    }

    /// <summary>
    /// Check if the database is loaded
    /// </summary>
    public bool IsLoaded => _database != null;

    /// <summary>
    /// Get when the database was last fetched
    /// </summary>
    public DateTime? LastFetchTime => _lastFetchTime;
}
