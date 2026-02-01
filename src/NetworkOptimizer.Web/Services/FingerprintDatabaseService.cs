using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Caches and provides access to the UniFi fingerprint database.
/// The database maps device IDs to names and categories.
/// </summary>
public class FingerprintDatabaseService : IFingerprintDatabaseService
{
    private readonly ILogger<FingerprintDatabaseService> _logger;
    private readonly UniFiConnectionService _connectionService;

    private UniFiFingerprintDatabase? _database;
    private DateTime? _lastFetchTime;
    private bool _lastFetchFailed;
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
    /// Gets the cached fingerprint database, fetching from the controller if the cache is expired or empty.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The fingerprint database, or null if not connected or fetch failed.</returns>
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
    /// Forces a refresh of the fingerprint database from the controller, bypassing the cache.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous refresh operation.</returns>
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
            _lastFetchFailed = true;
            return;
        }

        try
        {
            _logger.LogInformation("Fetching fingerprint database from UniFi controller...");

            _database = await _connectionService.Client.GetCompleteFingerprintDatabaseAsync(cancellationToken);

            // Only cache if we got actual data - don't poison cache with empty results
            // (empty results happen when Console can't reach UI.com)
            if (_database != null && _database.DevTypeIds.Count > 0)
            {
                _lastFetchTime = DateTime.UtcNow;
                _lastFetchFailed = false;
                _logger.LogInformation(
                    "Fingerprint database loaded: {DevTypes} device types, {Vendors} vendors, {Devices} specific devices",
                    _database.DevTypeIds.Count,
                    _database.VendorIds.Count,
                    _database.DevIds.Count);
            }
            else
            {
                _lastFetchFailed = true;
                _logger.LogWarning(
                    "Fingerprint database fetch returned empty results - Console may not have HTTPS access to *.ui.com. Will retry on next request.");
            }
        }
        catch (Exception ex)
        {
            _lastFetchFailed = true;
            _logger.LogError(ex, "Failed to fetch fingerprint database");
        }
    }

    /// <summary>
    /// Looks up a device name by its device ID (used for dev_id_override).
    /// </summary>
    /// <param name="deviceId">The device ID to look up.</param>
    /// <returns>The device name if found, otherwise null.</returns>
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
    /// Looks up a device type name by its ID (used for dev_cat).
    /// </summary>
    /// <param name="devTypeId">The device type ID to look up.</param>
    /// <returns>The device type name if found, otherwise null.</returns>
    public string? GetDeviceTypeName(int? devTypeId) =>
        _database?.GetDeviceTypeName(devTypeId);

    /// <summary>
    /// Looks up a vendor name by its ID.
    /// </summary>
    /// <param name="vendorId">The vendor ID to look up.</param>
    /// <returns>The vendor name if found, otherwise null.</returns>
    public string? GetVendorName(int? vendorId) =>
        _database?.GetVendorName(vendorId);

    /// <summary>
    /// Gets the device type ID for a specific device (from dev_ids lookup).
    /// </summary>
    /// <param name="deviceId">The device ID to look up.</param>
    /// <returns>The device type ID if found, otherwise null.</returns>
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
    /// Check if the database is loaded with data
    /// </summary>
    public bool IsLoaded => _database != null && _database.DevTypeIds.Count > 0;

    /// <summary>
    /// Check if the last fetch attempt failed or returned empty results.
    /// This indicates the Console may not have HTTPS access to *.ui.com.
    /// </summary>
    public bool LastFetchFailed => _lastFetchFailed;

    /// <summary>
    /// Get when the database was last successfully fetched
    /// </summary>
    public DateTime? LastFetchTime => _lastFetchTime;
}
