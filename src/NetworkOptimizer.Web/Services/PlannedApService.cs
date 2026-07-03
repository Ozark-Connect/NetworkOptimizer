using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// CRUD service for planned (hypothetical) APs used in coverage planning.
/// </summary>
public class PlannedApService
{
    private readonly NetworkOptimizer.Storage.Services.SiteDbContextFactory _siteDbFactory;
    private readonly SiteContextService _siteContext;
    private readonly ILogger<PlannedApService> _logger;

    public PlannedApService(
        NetworkOptimizer.Storage.Services.SiteDbContextFactory siteDbFactory,
        SiteContextService siteContext,
        ILogger<PlannedApService> logger)
    {
        _siteDbFactory = siteDbFactory;
        _siteContext = siteContext;
        _logger = logger;
    }

    /// <summary>Context for the current site's database (planned APs are per-site rows).</summary>
    private NetworkOptimizerDbContext CreateSiteDb() =>
        _siteDbFactory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);

    public async Task<List<PlannedAp>> GetAllAsync()
    {
        using var db = CreateSiteDb();
        return await db.PlannedAps.OrderBy(a => a.CreatedAt).ToListAsync();
    }

    public async Task<PlannedAp> CreateAsync(PlannedAp ap)
    {
        using var db = CreateSiteDb();
        ap.CreatedAt = DateTime.UtcNow;
        ap.UpdatedAt = DateTime.UtcNow;
        db.PlannedAps.Add(ap);
        await db.SaveChangesAsync();
        _logger.LogInformation("Created planned AP {Id} ({Model}) at ({Lat}, {Lng})", ap.Id, ap.Model, ap.Latitude, ap.Longitude);
        return ap;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        using var db = CreateSiteDb();
        var ap = await db.PlannedAps.FindAsync(id);
        if (ap == null) return false;
        db.PlannedAps.Remove(ap);
        await db.SaveChangesAsync();
        _logger.LogInformation("Deleted planned AP {Id}", id);
        return true;
    }

    public async Task UpdateLocationAsync(int id, double lat, double lng)
    {
        using var db = CreateSiteDb();
        var ap = await db.PlannedAps.FindAsync(id);
        if (ap == null) return;
        ap.Latitude = lat;
        ap.Longitude = lng;
        ap.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task UpdateFloorAsync(int id, int floor)
    {
        using var db = CreateSiteDb();
        var ap = await db.PlannedAps.FindAsync(id);
        if (ap == null) return;
        ap.Floor = floor;
        ap.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task UpdateOrientationAsync(int id, int deg)
    {
        using var db = CreateSiteDb();
        var ap = await db.PlannedAps.FindAsync(id);
        if (ap == null) return;
        ap.OrientationDeg = deg;
        ap.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task UpdateMountTypeAsync(int id, string mountType)
    {
        using var db = CreateSiteDb();
        var ap = await db.PlannedAps.FindAsync(id);
        if (ap == null) return;
        ap.MountType = mountType;
        ap.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task UpdateTxPowerAsync(int id, string band, int? txPower)
    {
        using var db = CreateSiteDb();
        var ap = await db.PlannedAps.FindAsync(id);
        if (ap == null) return;
        switch (band)
        {
            case "2.4": ap.TxPower24Dbm = txPower; break;
            case "5": ap.TxPower5Dbm = txPower; break;
            case "6": ap.TxPower6Dbm = txPower; break;
        }
        ap.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task UpdateAntennaModeAsync(int id, string? mode)
    {
        using var db = CreateSiteDb();
        var ap = await db.PlannedAps.FindAsync(id);
        if (ap == null) return;
        ap.AntennaMode = mode;
        ap.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task UpdateNameAsync(int id, string name)
    {
        using var db = CreateSiteDb();
        var ap = await db.PlannedAps.FindAsync(id);
        if (ap == null) return;
        ap.Name = name;
        ap.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }
}
