using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for managing buildings, floor plans, and floor plan images.
/// </summary>
public class FloorPlanService
{
    private readonly NetworkOptimizer.Storage.Services.SiteDbContextFactory _siteDbFactory;
    private readonly SiteContextService _siteContext;
    private readonly ILogger<FloorPlanService> _logger;
    private readonly string _floorPlanDirectory;

    public FloorPlanService(
        NetworkOptimizer.Storage.Services.SiteDbContextFactory siteDbFactory,
        SiteContextService siteContext,
        ILogger<FloorPlanService> logger)
    {
        _siteDbFactory = siteDbFactory;
        _siteContext = siteContext;
        _logger = logger;
        _floorPlanDirectory = GetFloorPlanDirectory(_siteContext.Slug, _siteContext.IsDefault);
        Directory.CreateDirectory(_floorPlanDirectory);
        _logger.LogDebug("Floor plan storage directory: {Directory}", _floorPlanDirectory);
    }

    /// <summary>
    /// Context for the current site's database. Buildings, floor plans, and their
    /// images are per-site rows; the main-DB factory would show the main site's
    /// buildings on every site's WiFi optimizer / floor plan / heatmap.
    /// </summary>
    private NetworkOptimizerDbContext CreateSiteDb() =>
        _siteDbFactory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);

    private static string GetFloorPlanDirectory(string slug, bool isDefault)
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

        // Building/floor IDs autoincrement per-site, so image files would collide across
        // sites under one shared folder. The default site keeps the original unprefixed
        // path (existing installs); secondary sites nest under their slug.
        var root = Path.Combine(baseDataPath, "floor-plans");
        return isDefault ? root : Path.Combine(root, slug);
    }

    // --- Building CRUD ---

    public async Task<List<Building>> GetBuildingsAsync()
    {
        using var db = CreateSiteDb();
        return await db.Buildings
            .Include(b => b.Floors).ThenInclude(f => f.Images)
            .OrderBy(b => b.Name).ToListAsync();
    }

    public async Task<Building?> GetBuildingAsync(int id)
    {
        using var db = CreateSiteDb();
        return await db.Buildings.Include(b => b.Floors).FirstOrDefaultAsync(b => b.Id == id);
    }

    public async Task<Building> CreateBuildingAsync(string name, double centerLat, double centerLng)
    {
        using var db = CreateSiteDb();
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
        using var db = CreateSiteDb();
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
        using var db = CreateSiteDb();
        var building = await db.Buildings
            .Include(b => b.Floors).ThenInclude(f => f.Images)
            .FirstOrDefaultAsync(b => b.Id == id);
        if (building == null) return false;

        // Delete all image files (legacy per-floor + multi-image)
        foreach (var floor in building.Floors)
        {
            foreach (var image in floor.Images)
                DeleteImageFile(image);
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
        using var db = CreateSiteDb();
        return await db.FloorPlans
            .Where(f => f.BuildingId == buildingId)
            .OrderBy(f => f.FloorNumber)
            .ToListAsync();
    }

    public async Task<FloorPlan?> GetFloorAsync(int floorId)
    {
        using var db = CreateSiteDb();
        return await db.FloorPlans.FindAsync(floorId);
    }

    public async Task<FloorPlan> CreateFloorAsync(int buildingId, int floorNumber, string label,
        double swLat, double swLng, double neLat, double neLng, string floorMaterial = "floor_wood")
    {
        using var db = CreateSiteDb();
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
        using var db = CreateSiteDb();
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
        using var db = CreateSiteDb();
        var floor = await db.FloorPlans.Include(f => f.Images).FirstOrDefaultAsync(f => f.Id == floorId);
        if (floor == null) return false;

        // Delete all image files for this floor
        foreach (var image in floor.Images)
            DeleteImageFile(image);
        DeleteFloorPlanImage(floor);

        db.FloorPlans.Remove(floor);
        await db.SaveChangesAsync();
        return true;
    }

    // --- Legacy single-image handling (kept for backward compat) ---

    public async Task SaveFloorImageAsync(int floorId, Stream imageStream)
    {
        using var db = CreateSiteDb();
        var floor = await db.FloorPlans.FindAsync(floorId);
        if (floor == null) return;

        var buildingDir = Path.Combine(_floorPlanDirectory, floor.BuildingId.ToString());
        Directory.CreateDirectory(buildingDir);

        var fileName = $"floor_{floor.FloorNumber}.png";
        var filePath = Path.Combine(buildingDir, fileName);

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

    // --- FloorPlanImage CRUD (multi-image per floor) ---

    public async Task<List<FloorPlanImage>> GetFloorImagesAsync(int floorPlanId)
    {
        using var db = CreateSiteDb();
        return await db.FloorPlanImages
            .Where(i => i.FloorPlanId == floorPlanId)
            .OrderBy(i => i.SortOrder)
            .ToListAsync();
    }

    public async Task<FloorPlanImage?> GetFloorImageAsync(int imageId)
    {
        using var db = CreateSiteDb();
        return await db.FloorPlanImages.FindAsync(imageId);
    }

    public async Task<FloorPlanImage> CreateFloorImageAsync(int floorPlanId, Stream imageStream,
        double swLat, double swLng, double neLat, double neLng, string label = "")
    {
        using var db = CreateSiteDb();
        var floor = await db.FloorPlans.FindAsync(floorPlanId);
        if (floor == null) throw new ArgumentException("Floor not found", nameof(floorPlanId));

        var image = new FloorPlanImage
        {
            FloorPlanId = floorPlanId,
            Label = label,
            SwLatitude = swLat,
            SwLongitude = swLng,
            NeLatitude = neLat,
            NeLongitude = neLng,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.FloorPlanImages.Add(image);
        await db.SaveChangesAsync();

        // Save image file using the generated ID, detecting format from stream header
        var buildingDir = Path.Combine(_floorPlanDirectory, floor.BuildingId.ToString());
        Directory.CreateDirectory(buildingDir);

        // Read first 12 bytes to detect image type, then reset stream
        var header = new byte[12];
        var headerRead = await imageStream.ReadAsync(header, 0, header.Length);
        var ext = ".png"; // default
        if (headerRead >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            ext = ".jpg";
        else if (headerRead >= 12 && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46
                 && header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
            ext = ".webp";
        if (imageStream.CanSeek) imageStream.Position = 0;

        var fileName = $"floor_{floor.FloorNumber}_img_{image.Id}{ext}";
        var filePath = Path.Combine(buildingDir, fileName);

        using (var fileStream = File.Create(filePath))
        {
            if (!imageStream.CanSeek)
            {
                // Stream was not seekable - write header bytes first, then rest
                await fileStream.WriteAsync(header, 0, headerRead);
            }
            await imageStream.CopyToAsync(fileStream);
        }

        image.ImagePath = Path.Combine(floor.BuildingId.ToString(), fileName);
        await db.SaveChangesAsync();

        _logger.LogInformation("Created floor plan image {ImageId} for floor {FloorId} at {Path}", image.Id, floorPlanId, filePath);
        return image;
    }

    public async Task<FloorPlanImage?> UpdateFloorImageAsync(int imageId, double? swLat = null, double? swLng = null,
        double? neLat = null, double? neLng = null, double? opacity = null, double? rotationDeg = null,
        string? cropJson = null, string? label = null)
    {
        using var db = CreateSiteDb();
        var image = await db.FloorPlanImages.FindAsync(imageId);
        if (image == null) return null;

        if (swLat.HasValue) image.SwLatitude = swLat.Value;
        if (swLng.HasValue) image.SwLongitude = swLng.Value;
        if (neLat.HasValue) image.NeLatitude = neLat.Value;
        if (neLng.HasValue) image.NeLongitude = neLng.Value;
        if (opacity.HasValue) image.Opacity = opacity.Value;
        if (rotationDeg.HasValue) image.RotationDeg = rotationDeg.Value;
        if (cropJson != null) image.CropJson = cropJson;
        if (label != null) image.Label = label;
        image.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        return image;
    }

    public async Task<bool> DeleteFloorImageAsync(int imageId)
    {
        using var db = CreateSiteDb();
        var image = await db.FloorPlanImages.FindAsync(imageId);
        if (image == null) return false;

        DeleteImageFile(image);
        db.FloorPlanImages.Remove(image);
        await db.SaveChangesAsync();
        _logger.LogInformation("Deleted floor plan image {ImageId}", imageId);
        return true;
    }

    public string? GetFloorImageFilePath(FloorPlanImage image)
    {
        if (string.IsNullOrEmpty(image.ImagePath)) return null;
        var fullPath = Path.Combine(_floorPlanDirectory, image.ImagePath);
        return File.Exists(fullPath) ? fullPath : null;
    }

    private void DeleteImageFile(FloorPlanImage image)
    {
        if (string.IsNullOrEmpty(image.ImagePath)) return;
        var fullPath = Path.Combine(_floorPlanDirectory, image.ImagePath);
        if (File.Exists(fullPath))
        {
            try { File.Delete(fullPath); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete floor plan image file {Path}", fullPath); }
        }
    }
}
