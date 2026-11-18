using System.Net;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>A city set by hand for a traced hop's address.</summary>
/// <param name="Address">The hop's address, in canonical form.</param>
/// <param name="City">The city's name.</param>
/// <param name="Region">The state or province, when known.</param>
/// <param name="CountryCode">ISO 3166-1 alpha-2, upper case.</param>
/// <param name="Latitude">The city's latitude, in degrees.</param>
/// <param name="Longitude">The city's longitude, in degrees.</param>
/// <param name="SetBy">Who set it.</param>
public sealed record HopLocationSetting(
    string Address, string City, string? Region, string CountryCode, double Latitude, double Longitude, string? SetBy);

/// <summary>A Set location editor's subject: the hop, where it is placed now, and the icon that opened it.</summary>
public sealed record HopLocationEdit(string Address, HopLocationView? Location, Microsoft.AspNetCore.Components.ElementReference Anchor);

/// <summary>
/// Cities set by hand for traced hops, by address: Network Tools' traceroute and ISP Health's hop
/// tables show them in place of the location read from the hop. Instance-wide: every site's traces
/// use them. Any role reads them; changing them is an Admin's. The SaaS fork partitions them by
/// decorating this interface.
/// </summary>
[MutatingService]
public interface IHopLocationOverrideService
{
    /// <summary>Every override, by address.</summary>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyDictionary<string, HopLocationSetting>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Sets the city for an address, replacing one already set.</summary>
    /// <exception cref="ArgumentException">The address, city, country, or coordinates do not validate.</exception>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.HopLocationSet, TargetType = "hop_location", InstanceScoped = true)]
    Task SetAsync(string address, string city, string? region, string countryCode, double latitude, double longitude,
        CancellationToken ct = default);

    /// <summary>Removes the city set for an address, so the hop's own location shows again.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.HopLocationCleared, TargetType = "hop_location", InstanceScoped = true)]
    Task ClearAsync(string address, CancellationToken ct = default);
}

/// <summary>
/// The cities set by hand that a site's traces use, for the app's own background work (the Path Map's
/// traces), which has no caller to pass the gate. Here every site uses the install's; the SaaS fork
/// replaces this to key them as it keys <see cref="IHopLocationOverrideService"/>.
/// </summary>
public interface IHopLocationOverrideSource
{
    /// <summary>The overrides a trace from <paramref name="siteSlug"/> uses, by address.</summary>
    Task<IReadOnlyDictionary<string, HopLocationSetting>> ForSiteAsync(string siteSlug, CancellationToken ct = default);
}

/// <summary>Every site uses the install's overrides, through the cache.</summary>
public sealed class InstallHopLocationOverrideSource : IHopLocationOverrideSource
{
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDb;
    private readonly HopLocationOverrideCache _cache;

    public InstallHopLocationOverrideSource(IDbContextFactory<NetworkOptimizerDbContext> mainDb, HopLocationOverrideCache cache)
    {
        _mainDb = mainDb;
        _cache = cache;
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, HopLocationSetting>> ForSiteAsync(string siteSlug, CancellationToken ct = default) =>
        HopLocationOverrideService.LoadAsync(_mainDb, _cache, ct);
}

/// <inheritdoc cref="IHopLocationOverrideService" />
public sealed class HopLocationOverrideService : IHopLocationOverrideService
{
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDb;
    private readonly HopLocationOverrideCache _cache;
    private readonly Identity.ICallerContext _caller;
    private readonly IAuditContext _audit;

    public HopLocationOverrideService(IDbContextFactory<NetworkOptimizerDbContext> mainDb, HopLocationOverrideCache cache,
        Identity.ICallerContext caller, IAuditContext audit)
    {
        _mainDb = mainDb;
        _cache = cache;
        _caller = caller;
        _audit = audit;
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, HopLocationSetting>> GetAllAsync(CancellationToken ct = default) =>
        LoadAsync(_mainDb, _cache, ct);

    /// <summary>The overrides, through the cache (<see cref="InstallHopLocationOverrideSource"/>).</summary>
    internal static async Task<IReadOnlyDictionary<string, HopLocationSetting>> LoadAsync(
        IDbContextFactory<NetworkOptimizerDbContext> mainDb, HopLocationOverrideCache cache, CancellationToken ct = default)
    {
        if (cache.Current is { } cached) return cached;
        var version = cache.Version;
        await using var db = await mainDb.CreateDbContextAsync(ct);
        var rows = await db.HopLocationOverrides.AsNoTracking().ToListAsync(ct);
        return cache.Set(rows.Select(ToSetting), version);
    }

    /// <inheritdoc />
    public async Task SetAsync(string address, string city, string? region, string countryCode, double latitude, double longitude,
        CancellationToken ct = default)
    {
        var set = Validate(address, city, region, countryCode, latitude, longitude);
        var canonical = set.Address;
        (city, region, countryCode) = (set.City, set.Region, set.CountryCode);

        await using var db = await _mainDb.CreateDbContextAsync(ct);
        var row = await db.HopLocationOverrides.FirstOrDefaultAsync(o => o.Address == canonical, ct);
        var before = row == null ? null : $"{row.City}, {row.CountryCode}";
        if (row == null)
        {
            row = new HopLocationOverride { Address = canonical };
            db.HopLocationOverrides.Add(row);
        }
        row.City = city;
        row.Region = region;
        row.CountryCode = countryCode;
        row.Latitude = latitude;
        row.Longitude = longitude;
        row.SetBy = _caller.Current?.ActorName;
        row.SetAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        _cache.Invalidate();

        _audit.SetTarget(canonical, canonical);
        _audit.SetDetails(new { address = canonical, city, region, country = countryCode, before });
    }

    /// <inheritdoc />
    public async Task ClearAsync(string address, CancellationToken ct = default)
    {
        var canonical = Canonical(address) ?? throw new ArgumentException("Not an IP address.", nameof(address));
        await using var db = await _mainDb.CreateDbContextAsync(ct);
        var row = await db.HopLocationOverrides.FirstOrDefaultAsync(o => o.Address == canonical, ct);
        _audit.SetTarget(canonical, canonical);
        if (row == null)
        {
            _audit.SuppressNoChange();
            return;
        }
        _audit.SetDetails(new { address = canonical, city = row.City, country = row.CountryCode });
        db.HopLocationOverrides.Remove(row);
        await db.SaveChangesAsync(ct);
        _cache.Invalidate();
    }

    /// <summary>
    /// The override <see cref="SetAsync"/> stores for these inputs: the address canonical, the names trimmed, the
    /// country upper case. <see cref="HopLocationSetting.SetBy"/> is null.
    /// </summary>
    /// <exception cref="ArgumentException">The address, city, country, or coordinates do not validate.</exception>
    public static HopLocationSetting Validate(string address, string city, string? region, string countryCode,
        double latitude, double longitude)
    {
        var canonical = Canonical(address) ?? throw new ArgumentException("Not an IP address.", nameof(address));
        city = city?.Trim() ?? "";
        countryCode = countryCode?.Trim().ToUpperInvariant() ?? "";
        if (city.Length == 0) throw new ArgumentException("A city is required.", nameof(city));
        if (city.Length > 120) throw new ArgumentException("The city's name is too long.", nameof(city));
        if (countryCode.Length != 2 || !countryCode.All(char.IsAsciiLetterUpper))
            throw new ArgumentException("A two-letter country code is required.", nameof(countryCode));
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180 || double.IsNaN(latitude) || double.IsNaN(longitude))
            throw new ArgumentException("The coordinates are out of range.");
        region = string.IsNullOrWhiteSpace(region) ? null : region.Trim();
        if (region is { Length: > 120 }) region = region[..120];
        return new HopLocationSetting(canonical, city, region, countryCode, latitude, longitude, null);
    }

    /// <summary>The address in canonical text form, or null when it is not one.</summary>
    internal static string? Canonical(string? address) =>
        IPAddress.TryParse(address?.Trim(), out var ip) ? ip.ToString() : null;

    private static HopLocationSetting ToSetting(HopLocationOverride o) =>
        new(o.Address, o.City, o.Region, o.CountryCode, o.Latitude, o.Longitude, o.SetBy);
}

/// <summary>
/// The overrides held in memory between changes: every trace render reads them, and they change only
/// when someone sets or clears one.
/// </summary>
public sealed class HopLocationOverrideCache
{
    private readonly object _lock = new();
    private IReadOnlyDictionary<string, HopLocationSetting>? _current;
    private int _version;

    /// <summary>The overrides as last read, or null when they must be read again.</summary>
    public IReadOnlyDictionary<string, HopLocationSetting>? Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>Counts changes: take it before a read and pass it to <see cref="Set"/>.</summary>
    public int Version
    {
        get { lock (_lock) return _version; }
    }

    /// <summary>
    /// Holds a fresh read and returns it. A read that began before a change is returned but not held,
    /// so it cannot stand in for the change.
    /// </summary>
    public IReadOnlyDictionary<string, HopLocationSetting> Set(IEnumerable<HopLocationSetting> settings, int version)
    {
        var map = settings.ToDictionary(s => s.Address, StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            if (version == _version) _current = map;
        }
        return map;
    }

    /// <summary>Drops the held read after a change.</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _current = null;
            _version++;
        }
    }
}
