using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Models;
using NetworkOptimizer.WiFi.Data;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Provides AP map marker data by joining UniFi AP snapshots with saved locations,
/// and handles persisting AP location changes.
/// </summary>
public class ApMapService
{
    private readonly WiFiOptimizerService _wifiService;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly ILogger<ApMapService> _logger;

    public ApMapService(WiFiOptimizerService wifiService, IDbContextFactory<NetworkOptimizerDbContext> dbFactory, ILogger<ApMapService> logger)
    {
        _wifiService = wifiService;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>
    /// Load AP map markers by joining UniFi AP snapshots with saved DB locations.
    /// </summary>
    public async Task<List<ApMapMarker>> GetApMapMarkersAsync()
    {
        var aps = await _wifiService.GetAccessPointsAsync();

        using var db = await _dbFactory.CreateDbContextAsync();
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
                    var catalogDefaults = ApModelCatalog.GetBandDefaults(ap.Model, bandStr);
                    var catalogMax = catalogDefaults.MaxTxPowerDbm;
                    var apiMax = r.MaxTxPower;
                    // Clamp API max to catalog max when API reports unrealistically high values
                    var clampedMax = (apiMax.HasValue && apiMax.Value > catalogMax) ? catalogMax : apiMax;
                    _logger.LogDebug("AP {Name} model='{Model}' band={Band} catalogMax={CatalogMax} apiMax={ApiMax} clampedMax={ClampedMax}",
                        ap.Name, ap.Model, bandStr, catalogMax, apiMax, clampedMax);
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
    /// Save an AP's map location (upsert by MAC address).
    /// </summary>
    public async Task SaveApLocationAsync(string mac, double lat, double lng)
    {
        var normalizedMac = mac.ToLowerInvariant();

        using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.ApLocations.FirstOrDefaultAsync(a => a.ApMac == normalizedMac);
        if (existing != null)
        {
            existing.Latitude = lat;
            existing.Longitude = lng;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            db.ApLocations.Add(new ApLocation
            {
                ApMac = normalizedMac,
                Latitude = lat,
                Longitude = lng,
                Floor = 1,
                UpdatedAt = DateTime.UtcNow
            });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Save an AP's floor assignment.
    /// </summary>
    public async Task SaveApFloorAsync(string mac, int floor)
    {
        var normalizedMac = mac.ToLowerInvariant();

        using var db = await _dbFactory.CreateDbContextAsync();
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

        using var db = await _dbFactory.CreateDbContextAsync();
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

        using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.ApLocations.FirstOrDefaultAsync(a => a.ApMac == normalizedMac);
        if (existing != null)
        {
            existing.MountType = mountType;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }
}
