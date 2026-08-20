using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.WiFi.Data;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Placing an access point on the map (design doc 06, gate 9).
///
/// The READ - <see cref="ApMapService.GetApMapMarkersAsync"/> - is deliberately not here. Every map
/// in the product draws AP markers from it, and a Viewer is meant to see maps, signal, AP locations
/// and RF propagation in full; gating the read would empty those maps for exactly the role that
/// exists to look at them. Only moving an AP is gated.
///
/// Site Operator: it records where we think a radio is, which changes what our own coverage and
/// signal views compute, and nothing on the network. The maps already offer a view-only mode - this
/// is the boundary behind it, because a hidden edit button is not one.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IApMapAdminService
{
    /// <summary>Moves an AP to a position (and optionally a floor and height).</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "ap_location")]
    Task SaveApLocationAsync(string mac, double lat, double lng, int? floor = null, double? heightM = null);

    /// <summary>Sets which floor an AP sits on.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "ap_location")]
    Task SaveApFloorAsync(string mac, int floor);

    /// <summary>Sets an AP's orientation in degrees.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "ap_location")]
    Task SaveApOrientationAsync(string mac, int orientationDeg);

    /// <summary>Sets how an AP is mounted (ceiling, wall, and so on).</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "ap_location")]
    Task SaveApMountTypeAsync(string mac, string mountType);

    /// <summary>Forgets a placement, returning the device to the layout engine.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "ap_location")]
    Task<bool> DeleteApLocationAsync(string mac);
}

/// <summary>
/// Provides AP map marker data by joining UniFi AP snapshots with saved locations,
/// and handles persisting AP location changes.
/// </summary>
public class ApMapService : IApMapAdminService
{
    private readonly WiFiOptimizerService _wifiService;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly NetworkOptimizer.Storage.Services.SiteDbContextFactory _siteDbFactory;
    private readonly SiteContextService _siteContext;
    private readonly ILogger<ApMapService> _logger;

    public ApMapService(
        WiFiOptimizerService wifiService,
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        NetworkOptimizer.Storage.Services.SiteDbContextFactory siteDbFactory,
        SiteContextService siteContext,
        ILogger<ApMapService> logger)
    {
        _wifiService = wifiService;
        _dbFactory = dbFactory;
        _siteDbFactory = siteDbFactory;
        _siteContext = siteContext;
        _logger = logger;
    }

    /// <summary>
    /// Context for the current site's database. AP/device placements are per-site
    /// rows; the main-DB factory would paint the main site's markers onto every
    /// site's map.
    /// </summary>
    private NetworkOptimizerDbContext CreateSiteDb() =>
        _siteDbFactory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);

    /// <summary>
    /// Load AP map markers by joining UniFi AP snapshots with saved DB locations.
    /// </summary>
    public async Task<List<ApMapMarker>> GetApMapMarkersAsync()
    {
        var aps = await _wifiService.GetAccessPointsAsync();

        using var db = CreateSiteDb();
        var savedLocations = await db.ApLocations.ToListAsync();
        var locationsByMac = savedLocations.ToDictionary(l => l.ApMac.ToLowerInvariant(), l => l);

        return aps.Select(ap =>
        {
            var mac = ap.Mac.ToLowerInvariant();
            locationsByMac.TryGetValue(mac, out var savedLocation);

            return new ApMapMarker
            {
                Mac = ap.Mac,
                Name = ap.Name,
                Model = ap.Model,
                Ip = ap.Ip,
                Latitude = savedLocation?.Latitude,
                Longitude = savedLocation?.Longitude,
                Floor = savedLocation?.Floor,
                OrientationDeg = savedLocation?.OrientationDeg ?? 0,
                MountType = MountTypeHelper.Resolve(savedLocation?.MountType, ap.Model),
                IsOnline = ap.IsOnline,
                TotalClients = ap.TotalClients,
                Radios = ap.Radios.Select(r =>
                {
                    var bandStr = r.Band.ToDisplayString();
                    var apiMax = r.MaxTxPower;
                    // Only clamp when API exceeds catalog by >= 2 dBm (small discrepancies
                    // are common between spec sheets and firmware, so allow 1 dBm tolerance)
                    int? clampedMax = apiMax;
                    if (ApModelCatalog.TryGetBandDefaults(ap.Model, bandStr, out var catalogDefaults) &&
                        apiMax.HasValue && apiMax.Value >= catalogDefaults.MaxTxPowerDbm + 2)
                    {
                        clampedMax = catalogDefaults.MaxTxPowerDbm;
                    }
                    _logger.LogTrace("AP {Name} model='{Model}' band={Band} apiMax={ApiMax} clampedMax={ClampedMax}",
                        ap.Name, ap.Model, bandStr, apiMax, clampedMax);
                    return new ApRadioSummary
                    {
                        Band = bandStr,
                        RadioCode = r.Band.ToUniFiCode(),
                        Channel = r.Channel,
                        ChannelWidth = r.ChannelWidth,
                        TxPowerDbm = r.TxPower,
                        MinTxPowerDbm = r.MinTxPower,
                        MaxTxPowerDbm = clampedMax,
                        Eirp = r.Eirp,
                        Clients = r.ClientCount,
                        Utilization = r.ChannelUtilization,
                        AntennaMode = r.AntennaMode
                    };
                }).ToList()
            };
        }).ToList();
    }

    /// <summary>
    /// Save an AP's map location (upsert by MAC address). heightM is the precise
    /// height above the floor's base elevation from 3D repositioning; callers that
    /// don't know it (Signal Map drags) omit it and any stored value is preserved.
    /// </summary>
    public async Task SaveApLocationAsync(string mac, double lat, double lng, int? floor = null, double? heightM = null)
    {
        var normalizedMac = mac.ToLowerInvariant();

        using var db = CreateSiteDb();
        var existing = await db.ApLocations.FirstOrDefaultAsync(a => a.ApMac == normalizedMac);
        if (existing != null)
        {
            existing.Latitude = lat;
            existing.Longitude = lng;
            if (floor.HasValue) existing.Floor = floor.Value;
            if (heightM.HasValue) existing.HeightM = heightM.Value;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            db.ApLocations.Add(new ApLocation
            {
                ApMac = normalizedMac,
                Latitude = lat,
                Longitude = lng,
                Floor = floor ?? 1,
                HeightM = heightM,
                UpdatedAt = DateTime.UtcNow
            });
        }
        await db.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task<bool> DeleteApLocationAsync(string mac)
    {
        var normalizedMac = mac.ToLowerInvariant();

        using var db = CreateSiteDb();
        var existing = await db.ApLocations.FirstOrDefaultAsync(a => a.ApMac == normalizedMac);
        if (existing == null) return false;

        db.ApLocations.Remove(existing);
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Save an AP's floor assignment.
    /// </summary>
    public async Task SaveApFloorAsync(string mac, int floor)
    {
        var normalizedMac = mac.ToLowerInvariant();

        using var db = CreateSiteDb();
        var existing = await db.ApLocations.FirstOrDefaultAsync(a => a.ApMac == normalizedMac);
        if (existing != null)
        {
            existing.Floor = floor;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Save an AP's orientation (azimuth in degrees, 0-359).
    /// </summary>
    public async Task SaveApOrientationAsync(string mac, int orientationDeg)
    {
        var normalizedMac = mac.ToLowerInvariant();

        using var db = CreateSiteDb();
        var existing = await db.ApLocations.FirstOrDefaultAsync(a => a.ApMac == normalizedMac);
        if (existing != null)
        {
            existing.OrientationDeg = orientationDeg;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Save an AP's mount type ("ceiling", "wall", or "desktop").
    /// </summary>
    public async Task SaveApMountTypeAsync(string mac, string mountType)
    {
        var normalizedMac = mac.ToLowerInvariant();

        using var db = CreateSiteDb();
        var existing = await db.ApLocations.FirstOrDefaultAsync(a => a.ApMac == normalizedMac);
        if (existing != null)
        {
            existing.MountType = mountType;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }
}
