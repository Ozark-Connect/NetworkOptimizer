using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Models;
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

    public ApMapService(WiFiOptimizerService wifiService, IDbContextFactory<NetworkOptimizerDbContext> dbFactory)
    {
        _wifiService = wifiService;
        _dbFactory = dbFactory;
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
                IsOnline = ap.IsOnline,
                TotalClients = ap.TotalClients,
                Radios = ap.Radios.Select(r => new ApRadioSummary
                {
                    Band = r.Band.ToDisplayString(),
                    RadioCode = r.Band.ToUniFiCode(),
                    Channel = r.Channel,
                    ChannelWidth = r.ChannelWidth,
                    TxPowerDbm = r.TxPower,
                    Eirp = r.Eirp,
                    Clients = r.ClientCount,
                    Utilization = r.ChannelUtilization,
                    AntennaMode = r.AntennaMode
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
}
