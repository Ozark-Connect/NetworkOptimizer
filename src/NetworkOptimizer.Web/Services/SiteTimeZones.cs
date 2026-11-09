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

    /// <summary>
    /// Loads the zones read before the last restart, so a schedule never runs on the server's clock
    /// while a console is first asked again. Called once at startup.
    /// </summary>
    Task LoadRememberedAsync();
}

/// <inheritdoc />
public sealed class SiteTimeZones : ISiteTimeZones
{
    private static readonly TimeSpan CacheLife = TimeSpan.FromHours(6);
    // No zone covers a legacy UniFi Network Server (no /api/system, a warning per read) as well as
    // an outage, so it is asked again hourly, not on every alert.
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);
    // A read that failed (a timeout, a console mid-reconnect) says nothing about the zone: ask again soon.
    private static readonly TimeSpan FailureRetryAfter = TimeSpan.FromMinutes(1);
    // The read sits in front of alert delivery; a console that hangs must not hold an alert.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<string, Func<CancellationToken, Task<string?>>?> _consoleReaderFor;
    private readonly ILogger<SiteTimeZones> _logger;
    private readonly ConcurrentDictionary<string, (TimeZoneInfo? Zone, DateTime CachedAt, bool Found)> _zones = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<TimeZoneInfo?>>> _reads = new();

    /// <summary>Setting that remembers each site's last zone ({"slug": "America/Chicago"}).</summary>
    public const string RememberedKey = "display.site_time_zones";

    private readonly Func<Task<string?>> _loadRemembered;
    private readonly Func<string, Task> _saveRemembered;
    private readonly ConcurrentDictionary<string, string> _remembered = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public SiteTimeZones(SiteConnectionRegistry siteConnections, ISystemSettingsService settings, ILogger<SiteTimeZones> logger)
        : this(slug => ConsoleReader(siteConnections, slug), logger,
            () => settings.GetGlobalAsync(RememberedKey), json => settings.SetGlobalAsync(RememberedKey, json))
    {
        // Read as each console connects, so the first page after a restart already has its zone.
        siteConnections.Connected += slug => _ = GetAsync(slug);
    }

    /// <param name="consoleReaderFor">
    /// Reads a site's console timezone id, or null when the site has no console to ask yet.
    /// </param>
    internal SiteTimeZones(
        Func<string, Func<CancellationToken, Task<string?>>?> consoleReaderFor,
        ILogger<SiteTimeZones> logger,
        Func<Task<string?>>? loadRemembered = null,
        Func<string, Task>? saveRemembered = null)
    {
        _consoleReaderFor = consoleReaderFor;
        _logger = logger;
        _loadRemembered = loadRemembered ?? (() => Task.FromResult<string?>(null));
        _saveRemembered = saveRemembered ?? (_ => Task.CompletedTask);
    }

    /// <inheritdoc />
    public async Task LoadRememberedAsync()
    {
        try
        {
            var json = await _loadRemembered();
            if (string.IsNullOrWhiteSpace(json)) return;
            var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
            foreach (var (slug, id) in map)
            {
                if (!TimeZoneCatalog.TryResolve(id, out var zone)) continue;
                _remembered[slug] = id;
                // Stale on purpose: it serves at once, and the first use reads the console again.
                _zones.TryAdd(slug, (zone, DateTime.MinValue, true));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load the remembered site time zones");
        }
    }

    // Never GetFor: that creates a connection, which would revive a removed or disabled site's.
    private static Func<CancellationToken, Task<string?>>? ConsoleReader(SiteConnectionRegistry sites, string slug)
    {
        if (!sites.TryGetExisting(slug, out var connection) || connection.Client is not { } client) return null;
        // The site's own locale first: it answers on every connection type and is per site. Then the
        // Network application's zone, then the console's own (local sign-in to a console only).
        return async ct =>
            await client.GetSiteTimeZoneAsync(ct)
            ?? NullIfBlank((await client.GetSystemInfoAsync(ct))?.Timezone)
            ?? (ConsoleCanGiveZone(client) ? (await client.GetConsoleSystemInfoAsync(ct))?.TimeZone : null);
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>
    /// Whether /api/system can give a zone. An API-key connection gets a reduced answer with no
    /// timezone, and a legacy UniFi Network Server has no such endpoint (its 401 forces a re-login).
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
        var failed = false;
        using var timeout = new CancellationTokenSource(ReadTimeout);
        try
        {
            // WaitAsync as well as the token: the client's retry delays do not observe it.
            TimeZoneCatalog.TryResolve(await read(timeout.Token).WaitAsync(ReadTimeout), out zone);
        }
        catch (Exception ex)
        {
            failed = true;
            _logger.LogDebug(ex, "Could not read the console time zone for site {Site}", slug);
        }
        if (zone != null)
        {
            _zones[slug] = (zone, DateTime.UtcNow, true);
            await RememberAsync(slug, zone);
        }
        else if (_zones.TryGetValue(slug, out var known) && known.Found)
        {
            // A failed refresh keeps the zone it had: dropping to the server's clock for an hour would
            // move every schedule on the site. It is asked again after RetryAfter.
            _zones[slug] = (known.Zone, DateTime.UtcNow - CacheLife + RetryAfter, true);
            zone = known.Zone;
        }
        else
        {
            // Back-dated so it lapses after FailureRetryAfter rather than the full RetryAfter.
            _zones[slug] = (null, failed ? DateTime.UtcNow - RetryAfter + FailureRetryAfter : DateTime.UtcNow, false);
        }
        _reads.TryRemove(slug, out _);
        return zone;
    }

    private async Task RememberAsync(string slug, TimeZoneInfo zone)
    {
        var id = TimeZoneCatalog.ToIana(zone);
        if (_remembered.TryGetValue(slug, out var known) && known == id) return;
        _remembered[slug] = id;
        await _saveLock.WaitAsync();
        try
        {
            await _saveRemembered(System.Text.Json.JsonSerializer.Serialize(new SortedDictionary<string, string>(_remembered)));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not remember the time zone for site {Site}", slug);
        }
        finally
        {
            _saveLock.Release();
        }
    }
}
