using System.Collections.Concurrent;
using NetworkOptimizer.Alerts.Interfaces;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The notification clock: the install default an admin chose (what the UI shows anyone without
/// their own zone), else the site's console zone, else the server's. The default is read through
/// <see cref="ITimeZonePreferenceSource.GetInheritedAsync"/>, so a host that decorates that seam
/// changes notifications too. Singleton, like the alert pipeline that calls it.
/// </summary>
public sealed class NotificationTimeZoneResolver : INotificationTimeZoneResolver
{
    private static readonly TimeSpan ConsoleZoneCacheLife = TimeSpan.FromHours(6);
    private static readonly TimeSpan ConsoleZoneRetryAfter = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Func<string, CancellationToken, Task<string?>> _readConsoleZone;
    private readonly ILogger<NotificationTimeZoneResolver> _logger;
    private readonly ConcurrentDictionary<string, (TimeZoneInfo? Zone, DateTime CachedAt, bool Found)> _consoleZones = new();

    public NotificationTimeZoneResolver(
        IServiceScopeFactory scopeFactory,
        SiteConnectionRegistry siteConnections,
        ILogger<NotificationTimeZoneResolver> logger)
        : this(scopeFactory, async (slug, ct) => siteConnections.GetFor(slug).Client is { } client
            ? (await client.GetConsoleSystemInfoAsync(ct))?.TimeZone
            : null, logger)
    {
    }

    /// <param name="readConsoleZone">Reads a site's console timezone id; null when it has none.</param>
    internal NotificationTimeZoneResolver(
        IServiceScopeFactory scopeFactory,
        Func<string, CancellationToken, Task<string?>> readConsoleZone,
        ILogger<NotificationTimeZoneResolver> logger)
    {
        _scopeFactory = scopeFactory;
        _readConsoleZone = readConsoleZone;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TimeZoneInfo> ResolveAsync(string? siteSlug, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var inherited = await scope.ServiceProvider.GetRequiredService<ITimeZonePreferenceSource>().GetInheritedAsync();
            if (TimeZoneCatalog.TryResolve(inherited, out var installDefault)) return installDefault;

            var slug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
            return await ConsoleZoneAsync(slug, cancellationToken) ?? TimeZoneInfo.Local;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not resolve the notification time zone for site {Site}; using the server's", siteSlug);
            return TimeZoneInfo.Local;
        }
    }

    /// <summary>The console's own zone, cached: it changes about never, and alerts can burst.</summary>
    private async Task<TimeZoneInfo?> ConsoleZoneAsync(string slug, CancellationToken cancellationToken)
    {
        if (_consoleZones.TryGetValue(slug, out var cached)
            && DateTime.UtcNow - cached.CachedAt < (cached.Found ? ConsoleZoneCacheLife : ConsoleZoneRetryAfter))
            return cached.Zone;

        TimeZoneInfo? zone = null;
        try
        {
            TimeZoneCatalog.TryResolve(await _readConsoleZone(slug, cancellationToken), out zone);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cached as not found below, so a console that is down is retried, not hammered.
            _logger.LogDebug(ex, "Could not read the console time zone for site {Site}", slug);
        }
        _consoleZones[slug] = (zone, DateTime.UtcNow, zone != null);
        return zone;
    }
}
