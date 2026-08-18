using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

/// <summary>
/// The install-wide firmware catalog in the main database, pooled from every site's catalog
/// refreshes. Every method is best-effort: a failed read or write costs a supplemental offer,
/// never the plan, so nothing here throws.
/// </summary>
public interface ISharedFirmwareCatalogRepository
{
    /// <summary>
    /// Records device builds a console just offered. Inserts new (Model, Channel, Version) rows
    /// and refreshes LastSeenUtc on ones already known.
    /// </summary>
    /// <param name="builds">Builds from one catalog refresh, with Model, Channel, Version, Url and Md5Sum set.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpsertDeviceBuildsAsync(IReadOnlyList<SharedFirmwareBuild> builds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a UniFi Network application build a console is offering. Inserts a new
    /// (Channel, Version) row or refreshes LastSeenUtc on one already known.
    /// </summary>
    /// <param name="channel">Channel the offering application follows.</param>
    /// <param name="version">Offered application version.</param>
    /// <param name="url">The .deb URL, when one was captured.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpsertNetworkAppBuildAsync(string channel, string version, string? url, CancellationToken cancellationToken = default);

    /// <summary>
    /// The newest known build for a model on a channel that is newer than the version given,
    /// or null when the catalog has nothing newer.
    /// </summary>
    /// <param name="model">Model code (catalog base_model).</param>
    /// <param name="channel">Channel the device is planned on.</param>
    /// <param name="thanVersion">The version the device runs now.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SharedFirmwareBuild?> FindNewerDeviceBuildAsync(string model, string channel, string? thanVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// The newest known Network application build on a channel that is newer than the version
    /// given, or null when the catalog has nothing newer.
    /// </summary>
    /// <param name="channel">Channel the application follows.</param>
    /// <param name="thanVersion">The version the application runs now.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SharedNetworkAppBuild?> FindNewerNetworkAppBuildAsync(string channel, string? thanVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Seeds the catalog from existing plan history. Extracts device builds from TargetImages
    /// and Network app entries from the plan document. Idempotent: existing rows are refreshed,
    /// not duplicated.
    /// </summary>
    /// <param name="planJsons">PlanJson values from FirmwareRolloutPlans across all site DBs.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task BackfillFromPlansAsync(IEnumerable<string> planJsons, CancellationToken cancellationToken = default);
}
