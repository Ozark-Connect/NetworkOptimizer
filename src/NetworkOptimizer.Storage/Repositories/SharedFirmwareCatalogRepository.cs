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

    /// <inheritdoc />
    public async Task BackfillFromPlansAsync(
        IEnumerable<string> planJsons, CancellationToken cancellationToken = default)
    {
        try
        {
            var deviceBuilds = new List<SharedFirmwareBuild>();
            var appBuilds = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            foreach (var json in planJsons)
            {
                if (string.IsNullOrWhiteSpace(json)) continue;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    // Device builds from TargetImages + wave steps (model) + channel groups (channel)
                    if (root.TryGetProperty("TargetImages", out var images)
                        && root.TryGetProperty("Waves", out var waves)
                        && root.TryGetProperty("ChannelGroups", out var groups))
                    {
                        // Build MAC → model from wave steps
                        var modelByMac = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        // Build MAC → wave number for channel group resolution
                        var waveByMac = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        foreach (var wave in waves.EnumerateArray())
                        {
                            var waveNum = wave.TryGetProperty("Number", out var wn) ? wn.GetInt32() : 0;
                            if (wave.TryGetProperty("Steps", out var steps))
                                foreach (var step in steps.EnumerateArray())
                                {
                                    var sMac = step.TryGetProperty("Mac", out var sm) ? sm.GetString() : null;
                                    if (string.IsNullOrEmpty(sMac)) continue;
                                    if (step.TryGetProperty("Model", out var mm) && mm.GetString() is { Length: > 0 } model)
                                        modelByMac[sMac] = model;
                                    waveByMac[sMac] = waveNum;
                                }
                        }

                        // Build wave-range → channel from channel groups
                        var channelRanges = new List<(int First, int Last, string Channel)>();
                        foreach (var group in groups.EnumerateArray())
                        {
                            var ch = group.TryGetProperty("Channel", out var chProp) ? chProp.GetString() : null;
                            var first = group.TryGetProperty("FirstWave", out var fw) ? fw.GetInt32() : 0;
                            var last = group.TryGetProperty("LastWave", out var lw) ? lw.GetInt32() : int.MaxValue;
                            if (!string.IsNullOrEmpty(ch))
                                channelRanges.Add((first, last, ch));
                        }

                        foreach (var img in images.EnumerateArray())
                        {
                            var mac = img.TryGetProperty("Mac", out var m) ? m.GetString() : null;
                            var ver = img.TryGetProperty("Version", out var v) ? v.GetString() : null;
                            var url = img.TryGetProperty("Url", out var u) ? u.GetString() : null;
                            if (string.IsNullOrEmpty(mac) || string.IsNullOrEmpty(ver) || string.IsNullOrEmpty(url))
                                continue;

                            if (!modelByMac.TryGetValue(mac, out var model) || string.IsNullOrEmpty(model))
                                continue;
                            waveByMac.TryGetValue(mac, out var waveNum);
                            var channel = channelRanges.FirstOrDefault(r => waveNum >= r.First && waveNum <= r.Last).Channel;
                            if (string.IsNullOrEmpty(channel)) continue;

                            deviceBuilds.Add(new SharedFirmwareBuild
                            {
                                Model = model, Channel = channel, Version = ver, Url = url,
                            });
                        }
                    }

                    // Network app from NetworkAppUpdate
                    if (root.TryGetProperty("IncludesUniFiNetworkUpdate", out var incNet)
                        && incNet.ValueKind == System.Text.Json.JsonValueKind.True
                        && root.TryGetProperty("NetworkAppUpdate", out var netApp))
                    {
                        var ver = netApp.TryGetProperty("TargetVersion", out var tv) ? tv.GetString() : null;
                        var url = netApp.TryGetProperty("Url", out var tu) ? tu.GetString() : null;
                        if (!string.IsNullOrEmpty(ver))
                        {
                            var key = $"beta|{ver}";
                            if (!appBuilds.ContainsKey(key))
                                appBuilds[key] = url;
                            else if (!string.IsNullOrEmpty(url))
                                appBuilds[key] = url;
                        }
                    }
                }
                catch (System.Text.Json.JsonException) { }
            }

            if (deviceBuilds.Count > 0)
                await UpsertDeviceBuildsAsync(deviceBuilds, cancellationToken);

            foreach (var (key, url) in appBuilds)
            {
                var parts = key.Split('|', 2);
                await UpsertNetworkAppBuildAsync(parts[0], parts[1], url, cancellationToken);
            }

            _logger.LogInformation(
                "Shared firmware catalog backfill: {Devices} device builds, {Apps} Network app builds",
                deviceBuilds.Count, appBuilds.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Shared firmware catalog backfill failed");
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
