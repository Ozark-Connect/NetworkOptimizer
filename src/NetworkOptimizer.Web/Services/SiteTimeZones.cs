using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Each site's own time zone, as its UniFi Console reports it. Shared by the viewer's clock and the
/// notification clock, so a page and an alert about one site read the same hours.
/// </summary>
public interface ISiteTimeZones
{
    /// <summary>
    /// The site's zone if it has been read, else null. Never waits on the console: a miss or a stale
    /// entry starts a read in the background, so a later caller has it.
    /// </summary>
    TimeZoneInfo? Cached(string slug);

    /// <summary>
    /// The site's zone, reading the console (bounded) when it is not cached. Null when the site has
    /// no console to ask yet, or its console cannot give one.
    /// </summary>
    Task<TimeZoneInfo?> GetAsync(string slug, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class SiteTimeZones : ISiteTimeZones
{
    private static readonly TimeSpan CacheLife = TimeSpan.FromHours(6);
    // No zone covers a legacy UniFi Network Server (no /api/system, a warning per read) as well as
    // an outage, so it is asked again hourly, not on every alert.
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);
    // The read sits in front of alert delivery; a console that hangs must not hold an alert.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<string, Func<CancellationToken, Task<string?>>?> _consoleReaderFor;
    private readonly ILogger<SiteTimeZones> _logger;
    private readonly ConcurrentDictionary<string, (TimeZoneInfo? Zone, DateTime CachedAt, bool Found)> _zones = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<TimeZoneInfo?>>> _reads = new();

    public SiteTimeZones(SiteConnectionRegistry siteConnections, ILogger<SiteTimeZones> logger)
        : this(slug => ConsoleReader(siteConnections, slug), logger)
    {
        // Read as each console connects, so the first page after a restart already has its zone.
        siteConnections.Connected += slug => _ = GetAsync(slug);
    }

    /// <param name="consoleReaderFor">
    /// Reads a site's console timezone id, or null when the site has no console to ask yet.
    /// </param>
    internal SiteTimeZones(
        Func<string, Func<CancellationToken, Task<string?>>?> consoleReaderFor,
        ILogger<SiteTimeZones> logger)
    {
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
    public TimeZoneInfo? Cached(string slug)
    {
        if (_zones.TryGetValue(slug, out var cached) && IsFresh(cached)) return cached.Zone;
        _ = GetAsync(slug);
        // A read that finished synchronously has already filled the cache; a stale entry still serves.
        return _zones.TryGetValue(slug, out cached) ? cached.Zone : null;
    }

    /// <inheritdoc />
    public async Task<TimeZoneInfo?> GetAsync(string slug, CancellationToken cancellationToken = default)
    {
        if (_zones.TryGetValue(slug, out var cached) && IsFresh(cached)) return cached.Zone;

        // Not connected yet (just after startup): nothing to cache, ask again next time.
        if (_consoleReaderFor(slug) is not { } read) return null;

        // One read per site at a time: a burst of alerts shares it rather than each asking the console.
        var inFlight = _reads.GetOrAdd(slug, _ => new Lazy<Task<TimeZoneInfo?>>(() => ReadAndCacheAsync(slug, read)));
        return await inFlight.Value.WaitAsync(cancellationToken);
    }

    private static bool IsFresh((TimeZoneInfo? Zone, DateTime CachedAt, bool Found) entry) =>
        DateTime.UtcNow - entry.CachedAt < (entry.Found ? CacheLife : RetryAfter);

    /// <summary>Not tied to any one caller's cancellation, since every waiting caller shares it.</summary>
    private async Task<TimeZoneInfo?> ReadAndCacheAsync(string slug, Func<CancellationToken, Task<string?>> read)
    {
        TimeZoneInfo? zone = null;
        using var timeout = new CancellationTokenSource(ReadTimeout);
        try
        {
            // WaitAsync as well as the token: the client's retry delays do not observe it.
            TimeZoneCatalog.TryResolve(await read(timeout.Token).WaitAsync(ReadTimeout), out zone);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the console time zone for site {Site}", slug);
        }
        _zones[slug] = (zone, DateTime.UtcNow, zone != null);
        _reads.TryRemove(slug, out _);
        return zone;
    }
}
