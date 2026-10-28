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
    // No zone covers a legacy UniFi Network Server (no /api/system, a warning per read) as well as
    // an outage, so it is asked again hourly, not on every alert.
    private static readonly TimeSpan ConsoleZoneRetryAfter = TimeSpan.FromHours(1);
    // The read sits in front of alert delivery; a console that hangs must not hold an alert.
    private static readonly TimeSpan ConsoleZoneReadTimeout = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Func<string, Func<CancellationToken, Task<string?>>?> _consoleReaderFor;
    private readonly ILogger<NotificationTimeZoneResolver> _logger;
    private readonly ConcurrentDictionary<string, (TimeZoneInfo? Zone, DateTime CachedAt, bool Found)> _consoleZones = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<TimeZoneInfo?>>> _reads = new();

    public NotificationTimeZoneResolver(
        IServiceScopeFactory scopeFactory,
        SiteConnectionRegistry siteConnections,
        ILogger<NotificationTimeZoneResolver> logger)
        : this(scopeFactory, slug => ConsoleReader(siteConnections, slug), logger)
    {
    }

    /// <param name="consoleReaderFor">
    /// Reads a site's console timezone id, or null when the site has no console to ask yet.
    /// </param>
    internal NotificationTimeZoneResolver(
        IServiceScopeFactory scopeFactory,
        Func<string, Func<CancellationToken, Task<string?>>?> consoleReaderFor,
        ILogger<NotificationTimeZoneResolver> logger)
    {
        _scopeFactory = scopeFactory;
        _consoleReaderFor = consoleReaderFor;
        _logger = logger;
    }

    // Never GetFor: that creates a connection, which would revive a removed or disabled site's.
    private static Func<CancellationToken, Task<string?>>? ConsoleReader(SiteConnectionRegistry sites, string slug)
    {
        if (!sites.TryGetExisting(slug, out var connection) || connection.Client is not { } client) return null;
        if (!ConsoleCanGiveZone(client)) return _ => Task.FromResult<string?>(null);
        return async ct => (await client.GetConsoleSystemInfoAsync(ct))?.TimeZone;
    }

    /// <summary>
    /// An API-key connection never answers /api/system, and a legacy UniFi Network Server has no such
    /// endpoint (its 401 forces a re-login): neither can give a zone, so neither is asked.
    /// </summary>
    internal static bool ConsoleCanGiveZone(UniFi.UniFiApiClient client) =>
        !client.UseApiKey && !client.IsStandaloneNetworkController;

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

        // Not connected yet (just after startup): nothing to cache, ask again next time.
        if (_consoleReaderFor(slug) is not { } read) return null;

        // One read per site at a time: a burst of alerts shares it rather than each asking the console.
        var inFlight = _reads.GetOrAdd(slug, _ => new Lazy<Task<TimeZoneInfo?>>(() => ReadAndCacheAsync(slug, read)));
        return await inFlight.Value.WaitAsync(cancellationToken);
    }

    /// <summary>Not tied to any one caller's cancellation, since every waiting caller shares it.</summary>
    private async Task<TimeZoneInfo?> ReadAndCacheAsync(string slug, Func<CancellationToken, Task<string?>> read)
    {
        TimeZoneInfo? zone = null;
        using var timeout = new CancellationTokenSource(ConsoleZoneReadTimeout);
        try
        {
            // WaitAsync as well as the token: the client's retry delays do not observe it.
            TimeZoneCatalog.TryResolve(await read(timeout.Token).WaitAsync(ConsoleZoneReadTimeout), out zone);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the console time zone for site {Site}", slug);
        }
        _consoleZones[slug] = (zone, DateTime.UtcNow, zone != null);
        _reads.TryRemove(slug, out _);
        return zone;
    }
}
