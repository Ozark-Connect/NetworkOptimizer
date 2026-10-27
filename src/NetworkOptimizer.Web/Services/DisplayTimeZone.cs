using Microsoft.AspNetCore.Components.Server.Circuits;
using NetworkOptimizer.Web.Services.Identity;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Where the viewer's time zone comes from: the caller's own choice, then the value a caller with no
/// choice inherits. Null at both layers means the server's zone, which is how an install that never
/// sets one keeps rendering exactly as before.
/// </summary>
public interface ITimeZonePreferenceSource
{
    /// <summary>The zone the current caller chose for themselves, or null.</summary>
    Task<string?> GetOwnAsync();

    /// <summary>The zone a caller with no choice of their own reads in, or null for the server zone.</summary>
    Task<string?> GetInheritedAsync();
}

/// <inheritdoc />
public sealed class TimeZonePreferenceSource : ITimeZonePreferenceSource
{
    /// <summary>
    /// Preference key. The install default is stored under it directly; a user's own value goes
    /// through <see cref="IUserPreferences"/>, which adds the user.
    /// </summary>
    public const string Key = "display.time_zone";

    private readonly ISystemSettingsService _settings;
    private readonly ICallerContext _caller;

    public TimeZonePreferenceSource(ISystemSettingsService settings, ICallerContext caller)
    {
        _settings = settings;
        _caller = caller;
    }

    /// <inheritdoc />
    public async Task<string?> GetOwnAsync() =>
        _caller.Current?.UserId is { } userId
            ? await _settings.GetGlobalAsync(UserDismissals.UserKey(Key, userId))
            : null;

    /// <inheritdoc />
    public Task<string?> GetInheritedAsync() => _settings.GetGlobalAsync(Key);
}

/// <summary>
/// The viewer's clock: the zone every timestamp on a page is shown in. Resolved once per circuit
/// (and on demand for an HTTP endpoint), so markup can convert synchronously. Until it resolves it
/// reads the server's zone.
///
/// Only for something a person reads. Schedule math that means "at the site" uses the site's console
/// zone, and background work has no viewer.
/// </summary>
public interface IDisplayTimeZone
{
    /// <summary>The resolved zone.</summary>
    TimeZoneInfo Zone { get; }

    /// <summary>The resolved zone's IANA id, for the browser.</summary>
    string ZoneId { get; }

    /// <summary>The current time on the viewer's clock.</summary>
    DateTime Now { get; }

    /// <summary>
    /// Converts an instant to the viewer's clock. Unspecified kind is read as UTC, which is what EF
    /// returns from SQLite and what <see cref="DateTime.ToLocalTime"/> assumed.
    /// </summary>
    DateTime ToLocal(DateTime utc);

    /// <inheritdoc cref="ToLocal(DateTime)"/>
    DateTime? ToLocal(DateTime? utc);

    /// <summary>
    /// Converts a wall-clock time the viewer entered back to UTC. A time inside a spring-forward gap
    /// moves to the first valid time after it.
    /// </summary>
    DateTime ToUtc(DateTime local);

    /// <summary>"CDT" for the viewer's zone at an instant.</summary>
    string Abbreviation(DateTime instantUtc);

    /// <summary>Re-reads the preference layers. Never throws: a failed read leaves the server zone.</summary>
    Task ResolveAsync();
}

/// <summary>Conversion to the viewer's clock at a call site.</summary>
public static class DisplayTimeExtensions
{
    /// <summary>
    /// The instant on the viewer's clock. Use this, never the parameterless BCL overload, which reads
    /// the server's zone.
    /// </summary>
    public static DateTime ToLocalTime(this DateTime utc, IDisplayTimeZone displayTime) => displayTime.ToLocal(utc);

    /// <inheritdoc cref="ToLocalTime(DateTime, IDisplayTimeZone)"/>
    public static DateTimeOffset ToLocalTime(this DateTimeOffset instant, IDisplayTimeZone displayTime) =>
        TimeZoneInfo.ConvertTime(instant, displayTime.Zone);
}

/// <inheritdoc />
public sealed class DisplayTimeZone : IDisplayTimeZone
{
    private readonly ITimeZonePreferenceSource _source;
    private readonly ILogger<DisplayTimeZone> _logger;
    private TimeZoneInfo _zone = TimeZoneInfo.Local;

    public DisplayTimeZone(ITimeZonePreferenceSource source, ILogger<DisplayTimeZone> logger)
    {
        _source = source;
        _logger = logger;
    }

    /// <inheritdoc />
    public TimeZoneInfo Zone => _zone;

    /// <inheritdoc />
    public string ZoneId => TimeZoneCatalog.ToIana(_zone);

    /// <inheritdoc />
    public DateTime Now => ToLocal(DateTime.UtcNow);

    /// <inheritdoc />
    public DateTime ToLocal(DateTime utc) => TimeZoneCatalog.ToZone(utc, _zone);

    /// <inheritdoc />
    public DateTime? ToLocal(DateTime? utc) => utc is { } value ? ToLocal(value) : null;

    /// <inheritdoc />
    public DateTime ToUtc(DateTime local)
    {
        if (local.Kind == DateTimeKind.Utc) return local;
        var wall = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (_zone.IsInvalidTime(wall)) wall = wall.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(wall, _zone);
    }

    /// <inheritdoc />
    public string Abbreviation(DateTime instantUtc) => TimeZoneCatalog.Abbreviation(_zone, instantUtc);

    /// <inheritdoc />
    public async Task ResolveAsync()
    {
        try
        {
            _zone = ResolveLayer(await _source.GetOwnAsync())
                ?? ResolveLayer(await _source.GetInheritedAsync())
                ?? TimeZoneInfo.Local;
        }
        catch (Exception ex)
        {
            // A settings read failing must not fail a page or a download; the server zone still reads right.
            _logger.LogWarning(ex, "Could not resolve the display time zone; using the server zone");
            _zone = TimeZoneInfo.Local;
        }
    }

    private TimeZoneInfo? ResolveLayer(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (TimeZoneCatalog.TryResolve(id, out var zone)) return zone;
        _logger.LogWarning("Display time zone {TimeZoneId} is not known on this platform; ignoring it", id);
        return null;
    }
}

/// <summary>
/// Resolves the viewer's zone when a circuit opens, before any component renders. Runs after
/// <see cref="CallerContextCircuitHandler"/>, which sets the user the preference belongs to.
/// </summary>
public sealed class DisplayTimeZoneCircuitHandler : CircuitHandler
{
    private readonly IDisplayTimeZone _displayTime;
    private readonly ILogger<DisplayTimeZoneCircuitHandler> _logger;

    public DisplayTimeZoneCircuitHandler(IDisplayTimeZone displayTime, ILogger<DisplayTimeZoneCircuitHandler> logger)
    {
        _displayTime = displayTime;
        _logger = logger;
    }

    /// <summary>After the caller context handler, which keeps the default order of 0.</summary>
    public override int Order => 100;

    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        try
        {
            await _displayTime.ResolveAsync();
        }
        catch (Exception ex)
        {
            // A settings read failing must not take the circuit down; the page reads the server zone.
            _logger.LogWarning(ex, "Could not resolve the display time zone; using the server zone");
        }
    }
}
