using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Caches and provides access to the UniFi fingerprint database.
/// The database maps device IDs to names and categories.
/// </summary>
public interface IFingerprintDatabaseService
{
    /// <summary>
    /// Gets whether the database has been loaded with data.
    /// </summary>
    bool IsLoaded { get; }

    /// <summary>
    /// Gets whether the last fetch attempt failed or returned empty results.
    /// This indicates the Console may not have HTTPS access to *.ui.com.
    /// </summary>
    bool LastFetchFailed { get; }

    /// <summary>
    /// Gets when the database was last successfully fetched.
    /// </summary>
    DateTime? LastFetchTime { get; }

    /// <summary>
    /// Get the cached fingerprint database, fetching if needed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The fingerprint database, or null if unavailable.</returns>
    Task<UniFiFingerprintDatabase?> GetDatabaseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Force refresh the fingerprint database.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Look up a device name by its device ID (used for dev_id_override).
    /// </summary>
    /// <param name="deviceId">The device ID to look up.</param>
    /// <returns>The device name, or null if not found.</returns>
    string? GetDeviceName(int? deviceId);

    /// <summary>
    /// Look up device type name by ID (used for dev_cat).
    /// </summary>
    /// <param name="devTypeId">The device type ID to look up.</param>
    /// <returns>The device type name, or null if not found.</returns>
    string? GetDeviceTypeName(int? devTypeId);

    /// <summary>
    /// Look up vendor name by ID.
    /// </summary>
    /// <param name="vendorId">The vendor ID to look up.</param>
    /// <returns>The vendor name, or null if not found.</returns>
    string? GetVendorName(int? vendorId);

    /// <summary>
    /// Get the device type ID for a specific device (from dev_ids lookup).
    /// </summary>
    /// <param name="deviceId">The device ID to look up.</param>
    /// <returns>The device type ID, or null if not found.</returns>
    int? GetDeviceTypeId(int? deviceId);
}
