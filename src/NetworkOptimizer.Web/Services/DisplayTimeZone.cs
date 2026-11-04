using Microsoft.AspNetCore.Components.Server.Circuits;
using NetworkOptimizer.Web.Services.Identity;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Where the viewer's chosen time zone comes from: the caller's own choice, then the install default
/// a caller with no choice inherits. Null at both layers means no one chose, and the viewer reads the
/// site's own zone (<see cref="IDisplayTimeZone.SiteZone"/>).
/// </summary>
public interface ITimeZonePreferenceSource
{
    /// <summary>The zone the current caller chose for themselves, or null.</summary>
    Task<string?> GetOwnAsync();

    /// <summary>The install default a caller with no choice of their own reads in, or null for none.</summary>
    Task<string?> GetInheritedAsync();

    /// <summary>
    /// The current caller's own time format ("12" or "24") and date order ("mdy" or "dmy"), each
    /// null when they have not chosen; the install default, then their browser's locale, decides then.
    /// </summary>
    Task<(string? Clock, string? Order)> GetOwnFormatAsync();

    /// <summary>
    /// The install default time format and date order a caller with no choice of their own reads in,
    /// and the format alert text is written in; each null for none.
    /// </summary>
    Task<(string? Clock, string? Order)> GetInheritedFormatAsync();
}

/// <inheritdoc />
public sealed class TimeZonePreferenceSource : ITimeZonePreferenceSource
{
    /// <summary>
    /// Preference key. The install default is stored under it directly; a user's own value goes
    /// through <see cref="IUserPreferences"/>, which adds the user.
    /// </summary>
    public const string Key = "display.time_zone";

    /// <summary>
    /// Time format, "12" or "24". The install default is stored under it directly; a user's own value
    /// goes through <see cref="IUserPreferences"/>.
    /// </summary>
    public const string ClockKey = "display.clock";

    /// <summary>
    /// Date order, "mdy" or "dmy". The install default is stored under it directly; a user's own value
    /// goes through <see cref="IUserPreferences"/>.
    /// </summary>
    public const string DateOrderKey = "display.date_order";

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

    /// <inheritdoc />
    public async Task<(string? Clock, string? Order)> GetOwnFormatAsync() =>
        _caller.Current?.UserId is { } userId
            ? (await _settings.GetGlobalAsync(UserDismissals.UserKey(ClockKey, userId)),
               await _settings.GetGlobalAsync(UserDismissals.UserKey(DateOrderKey, userId)))
            : (null, null);

    /// <inheritdoc />
    public async Task<(string? Clock, string? Order)> GetInheritedFormatAsync() =>
        (await _settings.GetGlobalAsync(ClockKey), await _settings.GetGlobalAsync(DateOrderKey));
}

/// <summary>
/// The viewer's clock: the zone every timestamp on a page is shown in. The user's own zone, else the
/// install default, else the console zone of the site in context, else the server's. The chosen
/// layers resolve once per circuit (and on demand for an HTTP endpoint), so markup can convert
/// synchronously.
///
/// Only for something a person reads. Schedule math that means "at the site" uses the site's console
/// zone, and background work has no viewer.
/// </summary>
public interface IDisplayTimeZone
{
    /// <summary>The resolved zone.</summary>
    TimeZoneInfo Zone { get; }

    /// <summary>How the viewer reads dates and times: their own choice, else the install default, else their browser's locale.</summary>
    DisplayFormat Format { get; }

    /// <summary>What the viewer's browser locale reads, for naming what "Default" means.</summary>
    DisplayFormat BrowserFormat { get; }

    /// <summary>The viewer's browser's own zone, offered as a choice; null when it has not said.</summary>
    TimeZoneInfo? BrowserZone { get; }

    /// <summary>
    /// The console zone of the site in context, or null when the console has not reported one. It
    /// is what a viewer with no zone of their own and no install default reads in.
    /// </summary>
    TimeZoneInfo? SiteZone { get; }

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

    /// <summary>Re-reads the preference layers. Never throws: a failed read leaves the site layer.</summary>
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

    /// <summary>
    /// Whether a time chart draws on the viewer's wall clock: only when their zone is not the browser's,
    /// or the browser has not said. Otherwise the chart is ApexCharts' own local mode on real instants.
    /// The twin of display-time.js toChart, which decides by the same two zone ids.
    /// </summary>
    public static bool ChartsOnWallClock(this IDisplayTimeZone displayTime) =>
        displayTime.BrowserZone is not { } browser || TimeZoneCatalog.ToIana(browser) != displayTime.ZoneId;

    /// <summary>
    /// An instant as a time chart's x: the instant itself, or on a wall-clock chart
    /// (<see cref="ChartsOnWallClock"/>) the viewer's local time marked UTC so the chart serializer
    /// sends it unshifted. Pairs with <see cref="ChartTimeFormat.ApplyTimeAxis"/>.
    /// </summary>
    public static DateTime ToChart(this IDisplayTimeZone displayTime, DateTime utc) =>
        displayTime.ChartsOnWallClock()
            ? DateTime.SpecifyKind(displayTime.ToLocal(utc), DateTimeKind.Utc)
            : DateTimeUtilities.AsUtc(utc);

    /// <summary>The instant a time chart x stands for; the inverse of <see cref="ToChart"/>.</summary>
    public static DateTime FromChart(this IDisplayTimeZone displayTime, DateTime chartX) =>
        displayTime.ChartsOnWallClock()
            ? displayTime.ToUtc(DateTime.SpecifyKind(chartX, DateTimeKind.Unspecified))
            : DateTimeUtilities.AsUtc(chartX);
}

/// <inheritdoc />
public sealed class DisplayTimeZone : IDisplayTimeZone
{
    private readonly ITimeZonePreferenceSource _source;
    private readonly ISiteTimeZones _siteZones;
    private readonly Func<string> _siteSlug;
    private readonly ILogger<DisplayTimeZone> _logger;
    private TimeZoneInfo? _chosen;
    private readonly Func<string?> _browserFormatCode;
    private readonly Func<string?> _browserZoneId;

    /// <summary>Cookie display-time.js writes with the browser's own IANA zone.</summary>
    public const string BrowserZoneCookie = "no-btz";

    /// <summary>
    /// Cookie display-time.js writes from the browser's locale ("12-mdy"), before the circuit opens,
    /// so the server formats a first page the way that browser reads.
    /// </summary>
    public const string BrowserFormatCookie = "no-dtf";

    // A known zone holds for the life of the circuit, so one page never shows two clocks; a site switch
    // reads the new site's. A miss holds only briefly, so a zone that arrives after the first page (a
    // console still connecting) is picked up rather than leaving the session on the server's clock.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (TimeZoneInfo? Zone, DateTime ReadAt)> _siteLayer = new();
    private static readonly TimeSpan SiteMissHold = TimeSpan.FromSeconds(10);

    public DisplayTimeZone(
        ITimeZonePreferenceSource source, ISiteTimeZones siteZones, SiteContextService siteContext,
        ILogger<DisplayTimeZone> logger, IHttpContextAccessor? httpContext = null)
        : this(source, siteZones, () => siteContext.Slug, logger,
            () => httpContext?.HttpContext?.Request.Cookies[BrowserFormatCookie],
            () => httpContext?.HttpContext?.Request.Cookies[BrowserZoneCookie])
    {
    }

    internal DisplayTimeZone(
        ITimeZonePreferenceSource source, ISiteTimeZones siteZones, Func<string> siteSlug,
        ILogger<DisplayTimeZone> logger, Func<string?>? browserFormatCode = null, Func<string?>? browserZoneId = null)
    {
        _browserFormatCode = browserFormatCode ?? (() => null);
        _browserZoneId = browserZoneId ?? (() => null);
        _source = source;
        _siteZones = siteZones;
        _siteSlug = siteSlug;
        _logger = logger;
    }

    /// <inheritdoc />
    public TimeZoneInfo Zone => _chosen ?? SiteZone ?? TimeZoneInfo.Local;

    /// <inheritdoc />
    public DisplayFormat Format { get; private set; } = DisplayFormat.Fallback;

    /// <inheritdoc />
    public DisplayFormat BrowserFormat { get; private set; } = DisplayFormat.Fallback;

    /// <inheritdoc />
    public TimeZoneInfo? BrowserZone { get; private set; }

    /// <inheritdoc />
    public TimeZoneInfo? SiteZone
    {
        get
        {
            string slug;
            try
            {
                slug = _siteSlug();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "No site in context for the display time zone");
                return null;
            }
            var now = DateTime.UtcNow;
            if (_siteLayer.TryGetValue(slug, out var held) && (held.Zone != null || now - held.ReadAt < SiteMissHold))
                return held.Zone;
            var zone = _siteZones.Cached(slug);
            _siteLayer[slug] = (zone, now);
            return zone;
        }
    }

    /// <inheritdoc />
    public string ZoneId => TimeZoneCatalog.ToIana(Zone);

    /// <inheritdoc />
    public DateTime Now => ToLocal(DateTime.UtcNow);

    /// <inheritdoc />
    public DateTime ToLocal(DateTime utc) => TimeZoneCatalog.ToZone(utc, Zone);

    /// <inheritdoc />
    public DateTime? ToLocal(DateTime? utc) => utc is { } value ? ToLocal(value) : null;

    /// <inheritdoc />
    public DateTime ToUtc(DateTime local)
    {
        if (local.Kind == DateTimeKind.Utc) return local;
        var zone = Zone;
        var wall = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(wall)) wall = wall.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(wall, zone);
    }

    /// <inheritdoc />
    public string Abbreviation(DateTime instantUtc) => TimeZoneCatalog.Abbreviation(Zone, instantUtc);

    /// <inheritdoc />
    public async Task ResolveAsync()
    {
        try
        {
            var (browserClock, browserOrder) = DisplayFormat.ParseCode(_browserFormatCode());
            BrowserFormat = new DisplayFormat(browserClock ?? DisplayFormat.Fallback.Clock, browserOrder ?? DisplayFormat.Fallback.Order);
            // The cookie is the browser's IANA id; the server token is not something a browser says.
            var browserId = _browserZoneId();
            BrowserZone = !string.Equals(browserId?.Trim(), TimeZoneCatalog.ServerToken, StringComparison.OrdinalIgnoreCase)
                && TimeZoneCatalog.TryResolve(browserId, out var browserZone) ? browserZone : null;
        }
        catch (Exception ex)
        {
            // No request to read (a scope with no HTTP context): the fallback stands.
            _logger.LogDebug(ex, "Could not read the browser's date and time format");
        }
        Format = BrowserFormat;

        try
        {
            _chosen = ResolveLayer(await _source.GetOwnAsync())
                ?? ResolveLayer(await _source.GetInheritedAsync());
            // Like the zone: the user's own choice, then the install default, then the browser's locale.
            Format = DisplayFormat.Layered(BrowserFormat, await _source.GetOwnFormatAsync(), await _source.GetInheritedFormatAsync());
        }
        catch (Exception ex)
        {
            // A settings read failing must not fail a page or a download; the site's zone still reads right.
            _logger.LogWarning(ex, "Could not resolve the display time zone; using the site's zone");
            _chosen = null;
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
            // A settings read failing must not take the circuit down; the page reads the site's zone.
            _logger.LogWarning(ex, "Could not resolve the display time zone; using the site's zone");
        }
    }
}
