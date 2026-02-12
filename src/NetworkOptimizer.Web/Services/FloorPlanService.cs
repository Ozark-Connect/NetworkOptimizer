using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for managing buildings, floor plans, and floor plan images.
/// </summary>
public class FloorPlanService
{
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly ILogger<FloorPlanService> _logger;
    private readonly string _floorPlanDirectory;

    public FloorPlanService(IDbContextFactory<NetworkOptimizerDbContext> dbFactory, ILogger<FloorPlanService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
        _floorPlanDirectory = GetFloorPlanDirectory();
        Directory.CreateDirectory(_floorPlanDirectory);
        _logger.LogInformation("Floor plan storage directory: {Directory}", _floorPlanDirectory);
    }

    private static string GetFloorPlanDirectory()
    {
        var isDocker = string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        string baseDataPath;
        if (isDocker)
        {
            baseDataPath = "/app/data";
        }
        else if (OperatingSystem.IsWindows())
        {
            baseDataPath = Path.Combine(AppContext.BaseDirectory, "data");
        }
        else
        {
            baseDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NetworkOptimizer");
        }

        return Path.Combine(baseDataPath, "floor-plans");
    }

    // --- Building CRUD ---

    public async Task<List<Building>> GetBuildingsAsync()
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Buildings.Include(b => b.Floors).OrderBy(b => b.Name).ToListAsync();
    }

    public async Task<Building?> GetBuildingAsync(int id)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Buildings.Include(b => b.Floors).FirstOrDefaultAsync(b => b.Id == id);
    }

    public async Task<Building> CreateBuildingAsync(string name, double centerLat, double centerLng)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var building = new Building
        {
            Name = name,
            CenterLatitude = centerLat,
            CenterLongitude = centerLng,
            CreatedAt = DateTime.UtcNow
        };
        db.Buildings.Add(building);
        await db.SaveChangesAsync();
        return building;
    }

    public async Task<Building?> UpdateBuildingAsync(int id, string name, double centerLat, double centerLng)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var building = await db.Buildings.FindAsync(id);
        if (building == null) return null;

        building.Name = name;
        building.CenterLatitude = centerLat;
        building.CenterLongitude = centerLng;
        await db.SaveChangesAsync();
        return building;
    }

    public async Task<bool> DeleteBuildingAsync(int id)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var building = await db.Buildings.Include(b => b.Floors).FirstOrDefaultAsync(b => b.Id == id);
        if (building == null) return false;

        // Delete floor plan images
        foreach (var floor in building.Floors)
        {
            DeleteFloorPlanImage(floor);
        }

        // Delete building directory
        var buildingDir = Path.Combine(_floorPlanDirectory, id.ToString());
        if (Directory.Exists(buildingDir))
        {
            try { Directory.Delete(buildingDir, true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete building directory {Dir}", buildingDir); }
        }

        db.Buildings.Remove(building);
        await db.SaveChangesAsync();
        return true;
    }

    // --- FloorPlan CRUD ---

    public async Task<List<FloorPlan>> GetFloorsAsync(int buildingId)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        return await db.FloorPlans
            .Where(f => f.BuildingId == buildingId)
            .OrderBy(f => f.FloorNumber)
            .ToListAsync();
    }

    public async Task<FloorPlan?> GetFloorAsync(int floorId)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        return await db.FloorPlans.FindAsync(floorId);
    }

    public async Task<FloorPlan> CreateFloorAsync(int buildingId, int floorNumber, string label,
        double swLat, double swLng, double neLat, double neLng, string floorMaterial = "floor_wood")
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var floor = new FloorPlan
        {
            BuildingId = buildingId,
            FloorNumber = floorNumber,
            Label = label,
            SwLatitude = swLat,
            SwLongitude = swLng,
            NeLatitude = neLat,
            NeLongitude = neLng,
            FloorMaterial = floorMaterial,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.FloorPlans.Add(floor);
        await db.SaveChangesAsync();
        return floor;
    }

    public async Task<FloorPlan?> UpdateFloorAsync(int floorId, double? swLat = null, double? swLng = null,
        double? neLat = null, double? neLng = null, double? opacity = null, string? wallsJson = null,
        string? label = null, string? floorMaterial = null)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var floor = await db.FloorPlans.FindAsync(floorId);
        if (floor == null) return null;

        if (swLat.HasValue) floor.SwLatitude = swLat.Value;
        if (swLng.HasValue) floor.SwLongitude = swLng.Value;
        if (neLat.HasValue) floor.NeLatitude = neLat.Value;
        if (neLng.HasValue) floor.NeLongitude = neLng.Value;
        if (opacity.HasValue) floor.Opacity = opacity.Value;
        if (wallsJson != null) floor.WallsJson = wallsJson;
        if (label != null) floor.Label = label;
        if (floorMaterial != null) floor.FloorMaterial = floorMaterial;
        floor.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        return floor;
    }

    public async Task<bool> DeleteFloorAsync(int floorId)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var floor = await db.FloorPlans.FindAsync(floorId);
        if (floor == null) return false;

        DeleteFloorPlanImage(floor);
        db.FloorPlans.Remove(floor);
        await db.SaveChangesAsync();
        return true;
    }

    // --- Image handling ---

    public async Task SaveFloorImageAsync(int floorId, Stream imageStream)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var floor = await db.FloorPlans.FindAsync(floorId);
        if (floor == null) return;

        var buildingDir = Path.Combine(_floorPlanDirectory, floor.BuildingId.ToString());
        Directory.CreateDirectory(buildingDir);

        var fileName = $"floor_{floor.FloorNumber}.png";
        var filePath = Path.Combine(buildingDir, fileName);

        // Save raw image (no resize - let browser handle scaling)
        using (var fileStream = File.Create(filePath))
        {
            await imageStream.CopyToAsync(fileStream);
        }

        floor.ImagePath = Path.Combine(floor.BuildingId.ToString(), fileName);
        floor.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        _logger.LogInformation("Saved floor plan image for floor {FloorId} at {Path}", floorId, filePath);
    }

    public string? GetFloorImagePath(FloorPlan floor)
    {
        if (string.IsNullOrEmpty(floor.ImagePath)) return null;
        var fullPath = Path.Combine(_floorPlanDirectory, floor.ImagePath);
        return File.Exists(fullPath) ? fullPath : null;
    }

    private void DeleteFloorPlanImage(FloorPlan floor)
    {
        if (string.IsNullOrEmpty(floor.ImagePath)) return;
        var fullPath = Path.Combine(_floorPlanDirectory, floor.ImagePath);
        if (File.Exists(fullPath))
        {
            try { File.Delete(fullPath); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete floor plan image {Path}", fullPath); }
        }
    }
}
