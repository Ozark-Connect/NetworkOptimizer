using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Repositories;

/// <inheritdoc />
/// <remarks>
/// Lives on the MAIN database whichever site is planning - the catalog's whole point is pooling
/// what every site's console has been offered. Singleton-safe: each call opens its own context.
/// </remarks>
public class SharedFirmwareCatalogRepository : ISharedFirmwareCatalogRepository
{
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly ILogger<SharedFirmwareCatalogRepository> _logger;

    public SharedFirmwareCatalogRepository(
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        ILogger<SharedFirmwareCatalogRepository> logger)
    {
        _mainDbFactory = mainDbFactory ?? throw new ArgumentNullException(nameof(mainDbFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task UpsertDeviceBuildsAsync(
        IReadOnlyList<SharedFirmwareBuild> builds, CancellationToken cancellationToken = default)
    {
        if (builds == null || builds.Count == 0) return;

        try
        {
            await using var db = await _mainDbFactory.CreateDbContextAsync(cancellationToken);
            var now = DateTime.UtcNow;

            foreach (var build in builds)
            {
                if (string.IsNullOrWhiteSpace(build.Model)
                    || string.IsNullOrWhiteSpace(build.Channel)
                    || string.IsNullOrWhiteSpace(build.Version)
                    || string.IsNullOrWhiteSpace(build.Url))
                {
                    continue;
                }

                var existing = await db.SharedFirmwareBuilds.FindAsync(
                    [build.Model, build.Channel, build.Version], cancellationToken);
                if (existing == null)
                {
                    db.SharedFirmwareBuilds.Add(new SharedFirmwareBuild
                    {
                        Model = build.Model,
                        Channel = build.Channel,
                        Version = build.Version,
                        Url = build.Url,
                        Md5Sum = build.Md5Sum,
                        FirstSeenUtc = now,
                        LastSeenUtc = now,
                    });
                }
                else
                {
                    existing.LastSeenUtc = now;
                    existing.Url = build.Url;
                    if (!string.IsNullOrWhiteSpace(build.Md5Sum))
                        existing.Md5Sum = build.Md5Sum;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not record device builds in the shared firmware catalog");
        }
    }

    /// <inheritdoc />
    public async Task UpsertNetworkAppBuildAsync(
        string channel, string version, string? url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(version)) return;

        try
        {
            await using var db = await _mainDbFactory.CreateDbContextAsync(cancellationToken);
            var now = DateTime.UtcNow;

            var existing = await db.SharedNetworkAppBuilds.FindAsync([channel, version], cancellationToken);
            if (existing == null)
            {
                db.SharedNetworkAppBuilds.Add(new SharedNetworkAppBuild
                {
                    Channel = channel,
                    Version = version,
                    Url = url,
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                });
            }
            else
            {
                existing.LastSeenUtc = now;
                if (!string.IsNullOrWhiteSpace(url))
                    existing.Url = url;
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not record a Network application build in the shared firmware catalog");
        }
    }

    /// <inheritdoc />
    public async Task<SharedFirmwareBuild?> FindNewerDeviceBuildAsync(
        string model, string channel, string? thanVersion, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(channel)) return null;

        try
        {
            await using var db = await _mainDbFactory.CreateDbContextAsync(cancellationToken);
            var rows = await db.SharedFirmwareBuilds.AsNoTracking()
                .Where(b => b.Model == model && b.Channel == channel)
                .ToListAsync(cancellationToken);
            return Newest(rows, thanVersion, b => b.Version);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read the shared firmware catalog for model {Model}", model);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<SharedNetworkAppBuild?> FindNewerNetworkAppBuildAsync(
        string channel, string? thanVersion, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channel)) return null;

        try
        {
            await using var db = await _mainDbFactory.CreateDbContextAsync(cancellationToken);
            var rows = await db.SharedNetworkAppBuilds.AsNoTracking()
                .Where(b => b.Channel == channel)
                .ToListAsync(cancellationToken);
            return Newest(rows, thanVersion, b => b.Version);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read the shared Network application catalog");
            return null;
        }
    }

    /// <summary>The newest row strictly newer than <paramref name="thanVersion"/>, or null.</summary>
    private static T? Newest<T>(IEnumerable<T> rows, string? thanVersion, Func<T, string> version) where T : class
    {
        T? best = null;
        foreach (var row in rows)
        {
            if (!FirmwareVersionFormat.IsNewer(version(row), thanVersion)) continue;
            if (best == null || FirmwareVersionFormat.IsNewer(version(row), version(best)))
                best = row;
        }
        return best;
    }
}
