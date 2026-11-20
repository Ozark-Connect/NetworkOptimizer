using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services.InnerSpace;

/// <summary>Why a listing has no templates, when it has none.</summary>
public enum InnerSpaceStatus
{
    Ok,
    /// <summary>The site has no working UniFi Console connection.</summary>
    NotConnected,
    /// <summary>The connection uses an API key, which InnerSpace refuses; it needs the username and password connection.</summary>
    ApiKeyOnly,
    /// <summary>The console account lacks admin access to InnerSpace.</summary>
    NotInnerSpaceAdmin,
    /// <summary>InnerSpace did not accept the console session.</summary>
    NoSession,
    /// <summary>InnerSpace is not installed on the console.</summary>
    NotInstalled,
    /// <summary>Anything else; see the detail.</summary>
    Error,
}

/// <summary>A floor of ours that an earlier import created from a source plan.</summary>
public sealed record ExistingImport(int FloorId, int BuildingId, string BuildingName, string FloorLabel, int FloorNumber,
    bool EditedSinceImport, Placement? Placement, int WallSegments);

/// <summary>A template and the floors earlier imports made from its plans.</summary>
public sealed record InnerSpaceTemplateInfo(ImportTemplate Template, IReadOnlyDictionary<string, ExistingImport> ExistingBySourcePlan);

/// <summary>A floor of a building already on the site, as the import's target choice needs it.</summary>
public sealed record SiteFloor(int Id, int FloorNumber, string Label, string? WallsJson);

/// <summary>A building already on the site.</summary>
public sealed record SiteBuilding(int Id, string Name, double CenterLat, double CenterLng, List<SiteFloor> Floors);

/// <summary>What InnerSpace holds for this site's console, and the site's own buildings to import into.</summary>
public sealed record InnerSpaceListing(InnerSpaceStatus Status, string? Detail, List<InnerSpaceTemplateInfo> Templates,
    List<SiteBuilding> Buildings);

/// <summary>Where one template floor goes: a new floor with this number, or over an existing floor.</summary>
public sealed record FloorTarget(string SourcePlanId, int FloorNumber, int? ReplaceFloorId);

/// <summary>
/// An import to apply. The service rebuilds the template from InnerSpace; only placement and choices
/// come from the caller. <paramref name="IncludeAps"/> false imports no APs and leaves existing ones alone.
/// </summary>
public sealed record InnerSpaceImportRequest(string TemplateId, Placement Placement, int? ExistingBuildingId,
    string? NewBuildingName, List<FloorTarget> Floors, bool IncludeAps = true);

/// <summary>A floor image the browser rasterizes and uploads (InnerSpace scans store SVG, which we do not store).</summary>
public sealed record PendingImage(int FloorId, string SourcePlanId, double SwLat, double SwLng, double NeLat, double NeLng, double RotationDeg);

/// <summary>What an import did.</summary>
public sealed record InnerSpaceImportResult(bool Success, string? Error, int BuildingId, List<int> FloorIds,
    int WallSegments, int PlannedAps, int PlacedAps, List<PendingImage> PendingImages);

/// <summary>Image bytes and their type, for the browser.</summary>
public sealed record InnerSpaceImage(byte[] Bytes, string ContentType);

/// <summary>
/// Importing UniFi InnerSpace floor plans (LiDAR scans, drawn plans, Design Center projects brought
/// into InnerSpace) as buildings on the Signal Map.
///
/// Site Operator throughout, matching the floor plan editor: an import writes our coverage model
/// (buildings, floors, walls, AP placement) and nothing on the network. The console reads need a
/// console login with InnerSpace admin; an API key is refused by InnerSpace.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IInnerSpaceImportService
{
    /// <summary>Reads InnerSpace on the site's console and returns the buildings it can import.</summary>
    [RequireRole(Roles.Operator)]
    Task<InnerSpaceListing> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>A floor image of a source plan, for the import preview and SVG rasterizing.</summary>
    [RequireRole(Roles.Operator)]
    Task<InnerSpaceImage?> GetImageAsync(string sourcePlanId, CancellationToken cancellationToken = default);

    /// <summary>Lays a template down as a new or existing building.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "floor_plan")]
    Task<InnerSpaceImportResult> ImportAsync(InnerSpaceImportRequest request, CancellationToken cancellationToken = default);
}

public sealed class InnerSpaceImportService : IInnerSpaceImportService
{
    /// <summary>Source reference prefix written on imported floors and planned APs.</summary>
    public const string SourcePrefix = "innerspace:";

    /// <summary>Label of a floor image the import created; a re-import replaces these.</summary>
    public const string ImageLabel = "InnerSpace";

    private const string ApiBase = "proxy/innerspace/integration/v1/";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly UniFiConnectionService _connection;
    private readonly NetworkOptimizer.Storage.Services.SiteDbContextFactory _siteDbFactory;
    private readonly SiteContextService _siteContext;
    private readonly HeatmapDataCache _heatmapCache;
    private readonly ILogger<InnerSpaceImportService> _logger;

    public InnerSpaceImportService(
        UniFiConnectionService connection,
        NetworkOptimizer.Storage.Services.SiteDbContextFactory siteDbFactory,
        SiteContextService siteContext,
        HeatmapDataCache heatmapCache,
        ILogger<InnerSpaceImportService> logger)
    {
        _connection = connection;
        _siteDbFactory = siteDbFactory;
        _siteContext = siteContext;
        _heatmapCache = heatmapCache;
        _logger = logger;
    }

    private NetworkOptimizerDbContext CreateSiteDb() => _siteDbFactory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);

    public async Task<InnerSpaceListing> ListAsync(CancellationToken cancellationToken = default)
    {
        var (status, detail, templates) = await LoadTemplatesAsync(cancellationToken);
        if (status != InnerSpaceStatus.Ok) return new InnerSpaceListing(status, detail, new(), new());

        using var db = CreateSiteDb();
        var buildings = (await db.Buildings.Include(b => b.Floors).OrderBy(b => b.Name).ToListAsync(cancellationToken))
            .Select(b => new SiteBuilding(b.Id, b.Name, b.CenterLatitude, b.CenterLongitude,
                b.Floors.OrderBy(f => f.FloorNumber).Select(f => new SiteFloor(f.Id, f.FloorNumber, f.Label, f.WallsJson)).ToList()))
            .ToList();
        var imported = await db.FloorPlans.Include(f => f.Building)
            .Where(f => f.SourceRef != null && f.SourceRef.StartsWith(SourcePrefix))
            .ToListAsync(cancellationToken);
        var bySource = imported.GroupBy(f => f.SourceRef!).ToDictionary(g => g.Key, g => g.OrderByDescending(f => f.SourceImportedAt).First());

        var infos = templates.Select(t =>
        {
            var existing = new Dictionary<string, ExistingImport>();
            foreach (var f in t.Floors)
            {
                if (!bySource.TryGetValue(SourcePrefix + f.SourcePlanId, out var floor)) continue;
                existing[f.SourcePlanId] = new ExistingImport(floor.Id, floor.BuildingId, floor.Building.Name, floor.Label,
                    floor.FloorNumber, floor.SourceImportedAt != null && floor.UpdatedAt > floor.SourceImportedAt.Value.AddSeconds(1),
                    ParsePlacement(floor.SourcePlacementJson), CountSegments(floor.WallsJson));
            }
            return new InnerSpaceTemplateInfo(t, existing);
        }).ToList();
        return new InnerSpaceListing(InnerSpaceStatus.Ok, null, infos, buildings);
    }

    public async Task<InnerSpaceImage?> GetImageAsync(string sourcePlanId, CancellationToken cancellationToken = default)
    {
        var client = _connection.Client;
        if (client == null || client.UseApiKey) return null;
        var project = await FetchProjectAsync(client, cancellationToken);
        var url = project.Project == null ? null
            : InnerSpaceShapes.From(project.Project.Shapes).Maps.FirstOrDefault(m => m.PlanId == sourcePlanId)?.UrlImage;
        var path = ConsolePath(url);
        if (path == null) return null;
        var (status, bytes) = await client.GetRawBytesAsync(UniFiApiScope.Console, path, cancellationToken);
        if (status != 200 || bytes.Length == 0) return null;
        return new InnerSpaceImage(bytes, SniffImageType(bytes));
    }

    public async Task<InnerSpaceImportResult> ImportAsync(InnerSpaceImportRequest request, CancellationToken cancellationToken = default)
    {
        InnerSpaceImportResult Fail(string error) => new(false, error, 0, new(), 0, 0, 0, new());

        var (status, detail, templates) = await LoadTemplatesAsync(cancellationToken);
        if (status != InnerSpaceStatus.Ok) return Fail(detail ?? status.ToString());
        var template = templates.FirstOrDefault(t => t.Id == request.TemplateId);
        if (template == null) return Fail("That InnerSpace plan is no longer on the console. Refresh and try again.");

        var client = _connection.Client!;
        var placement = request.Placement;
        using var db = CreateSiteDb();

        // The component checks these too; the service is the boundary.
        var newNumbers = request.Floors.Where(f => f.ReplaceFloorId == null).Select(f => f.FloorNumber).ToList();
        if (newNumbers.Count != newNumbers.Distinct().Count())
            return Fail("Two floors have the same floor number.");
        if (request.ExistingBuildingId is { } existingId
            && await db.FloorPlans.AnyAsync(f => f.BuildingId == existingId && newNumbers.Contains(f.FloorNumber), cancellationToken))
            return Fail("That floor number is taken in this building. Pick another, or replace that floor.");

        // All floors land or none do; image files written before a failure are orphans the
        // floor plan directory tolerates.
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        Building building;
        if (request.ExistingBuildingId is { } bid)
        {
            building = await db.Buildings.FirstOrDefaultAsync(b => b.Id == bid, cancellationToken)
                ?? throw new InvalidOperationException("The building no longer exists.");
        }
        else
        {
            building = new Building
            {
                Name = Truncate(string.IsNullOrWhiteSpace(request.NewBuildingName) ? template.Name : request.NewBuildingName.Trim(), 100),
                CenterLatitude = placement.Lat,
                CenterLongitude = placement.Lng,
                CreatedAt = DateTime.UtcNow,
            };
            db.Buildings.Add(building);
            await db.SaveChangesAsync(cancellationToken);
        }

        var now = DateTime.UtcNow;
        var placementJson = JsonSerializer.Serialize(new { lat = placement.Lat, lng = placement.Lng, rotationDeg = placement.RotationDeg });
        var floorIds = new List<int>();
        var pending = new List<PendingImage>();
        int planned = 0, placed = 0, segments = 0;

        foreach (var target in request.Floors)
        {
            var src = template.Floors.FirstOrDefault(f => f.SourcePlanId == target.SourcePlanId);
            if (src == null) continue;
            var sourceRef = SourcePrefix + src.SourcePlanId;
            var bounds = TemplatePlacer.FloorBounds(src, placement);

            FloorPlan floor;
            if (target.ReplaceFloorId is { } fid)
            {
                floor = await db.FloorPlans.Include(f => f.Images).FirstOrDefaultAsync(f => f.Id == fid, cancellationToken)
                    ?? throw new InvalidOperationException("The floor to replace no longer exists.");
                if (floor.BuildingId != building.Id)
                    throw new InvalidOperationException("The floor to replace is in a different building.");
                foreach (var img in floor.Images.Where(i => i.Label == ImageLabel).ToList())
                {
                    DeleteImageFile(img.ImagePath);
                    db.FloorPlanImages.Remove(img);
                }
                // Planned APs an earlier import of this source made; the user's own stay. Without
                // APs in this import, the earlier ones stay too.
                if (request.IncludeAps)
                    db.PlannedAps.RemoveRange(db.PlannedAps.Where(a => a.SourceRef == sourceRef));
            }
            else
            {
                floor = new FloorPlan
                {
                    BuildingId = building.Id,
                    FloorNumber = target.FloorNumber,
                    Label = Truncate(src.Label, 50),
                    CreatedAt = now,
                };
                db.FloorPlans.Add(floor);
            }

            floor.SwLatitude = bounds.SwLat;
            floor.SwLongitude = bounds.SwLng;
            floor.NeLatitude = bounds.NeLat;
            floor.NeLongitude = bounds.NeLng;
            floor.WallsJson = TemplatePlacer.WallsJson(src, placement);
            if (src.FloorMaterial != null) floor.FloorMaterial = src.FloorMaterial;
            floor.HeightM = src.HeightM ?? floor.HeightM;
            floor.SourceRef = sourceRef;
            floor.SourcePlacementJson = placementJson;
            floor.SourceImportedAt = now;
            floor.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            floorIds.Add(floor.Id);
            segments += src.Walls.Sum(w => w.Materials.Count);

            foreach (var ap in request.IncludeAps ? src.Aps : Enumerable.Empty<ImportAp>())
            {
                var (lat, lng) = TemplatePlacer.ToLatLng(ap.Position, placement);
                var orientation = TemplatePlacer.Orientation(ap, placement);
                if (ap.Mac != null)
                {
                    var loc = await db.ApLocations.FirstOrDefaultAsync(l => l.ApMac == ap.Mac, cancellationToken);
                    if (loc == null) db.ApLocations.Add(loc = new ApLocation { ApMac = ap.Mac });
                    loc.Latitude = lat;
                    loc.Longitude = lng;
                    loc.Floor = floor.FloorNumber;
                    loc.OrientationDeg = orientation;
                    loc.MountType = ap.MountType ?? loc.MountType;
                    loc.HeightM = ap.HeightM;
                    loc.UpdatedAt = now;
                    placed++;
                }
                else
                {
                    db.PlannedAps.Add(new PlannedAp
                    {
                        Name = Truncate(ap.Name, 100),
                        Model = Truncate(ap.Model, 50),
                        Latitude = lat,
                        Longitude = lng,
                        Floor = floor.FloorNumber,
                        OrientationDeg = orientation,
                        MountType = ap.MountType ?? "ceiling",
                        AntennaMode = ap.AntennaMode is { Length: <= 20 } mode ? mode : null,
                        SourceRef = sourceRef,
                        CreatedAt = now,
                        UpdatedAt = now,
                    });
                    planned++;
                }
            }
            await db.SaveChangesAsync(cancellationToken);

            if (src.Image != null)
            {
                var ib = TemplatePlacer.ImageBounds(src.Image, placement);
                var image = await FetchBytesAsync(client, src.Image.SourceUrl, cancellationToken);
                var type = image == null ? null : SniffImageType(image);
                if (image != null && type is "image/png" or "image/jpeg" or "image/webp")
                {
                    await SaveImageAsync(db, floor, image, type!, ib, cancellationToken);
                }
                else if (type == "image/svg+xml")
                {
                    pending.Add(new PendingImage(floor.Id, src.SourcePlanId, ib.SwLat, ib.SwLng, ib.NeLat, ib.NeLng, ib.RotationDeg));
                }
            }
        }

        await tx.CommitAsync(cancellationToken);
        _heatmapCache.Invalidate();
        _logger.LogInformation("InnerSpace import {Template}: building {Building}, {Floors} floors, {Segments} wall segments, {Planned} planned APs, {Placed} placed APs",
            template.Name, building.Id, floorIds.Count, segments, planned, placed);
        return new InnerSpaceImportResult(true, null, building.Id, floorIds, segments, planned, placed, pending);
    }

    // ---- Console reads ----

    private sealed record ProjectFetch(InnerSpaceStatus Status, string? Detail, InnerSpaceProject? Project);

    private async Task<(InnerSpaceStatus, string?, List<ImportTemplate>)> LoadTemplatesAsync(CancellationToken ct)
    {
        var client = _connection.Client;
        if (client == null || !_connection.IsConnected)
            return (InnerSpaceStatus.NotConnected, "The site is not connected to a UniFi Console.", new());
        if (client.UseApiKey)
            return (InnerSpaceStatus.ApiKeyOnly, "InnerSpace does not accept API keys.", new());

        var fetch = await FetchProjectAsync(client, ct);
        if (fetch.Status != InnerSpaceStatus.Ok || fetch.Project == null)
            return (fetch.Status, fetch.Detail, new());
        var project = fetch.Project;

        var sizes = new Dictionary<string, (double Width, double Height)>();
        var plansResp = await client.SendRawAsync(UniFiApiScope.Console, HttpMethod.Get, ApiBase + "floor_plans", cancellationToken: ct);
        if (plansResp.Status == 200)
        {
            try
            {
                using var doc = JsonDocument.Parse(plansResp.Body);
                foreach (var p in doc.RootElement.GetProperty("floor_plans").EnumerateArray())
                    if (p.TryGetProperty("id", out var id) && p.TryGetProperty("width", out var w) && p.TryGetProperty("height", out var h))
                        sizes[id.GetString()!] = (w.GetDouble(), h.GetDouble());
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "InnerSpace floor_plans response was not readable; floor images will be skipped");
            }
        }

        var scans = new Dictionary<string, ScanInfo>();
        foreach (var map in InnerSpaceShapes.From(project.Shapes).Maps.Where(m => m.PlanId != null && m.UrlModel != null))
        {
            var zip = await FetchBytesAsync(client, map.AdditionalZipUrl, ct);
            var gltfBytes = await FetchBytesAsync(client, map.UrlModel, ct);
            var gltf = gltfBytes == null ? null : System.Text.Encoding.UTF8.GetString(gltfBytes);
            scans[map.PlanId!] = RoomPlanWindows.ReadScan(zip, gltf);
        }

        string exterior;
        using (var db = CreateSiteDb())
            exterior = FloorPlanWallMaterials.SiteExteriorMaterial(await db.FloorPlans.Select(f => f.WallsJson).ToListAsync(ct));

        return (InnerSpaceStatus.Ok, null, InnerSpaceMapper.Build(project, sizes, scans, exterior));
    }

    private async Task<ProjectFetch> FetchProjectAsync(UniFiApiClient client, CancellationToken ct)
    {
        var resp = await client.SendRawAsync(UniFiApiScope.Console, HttpMethod.Get, ApiBase + "project?mode=2D", cancellationToken: ct);
        switch (resp.Status)
        {
            case 200:
                try
                {
                    var project = JsonSerializer.Deserialize<InnerSpaceProjectResponse>(resp.Body, JsonOpts)?.Data;
                    return project == null
                        ? new(InnerSpaceStatus.Error, "InnerSpace returned an empty project.", null)
                        : new(InnerSpaceStatus.Ok, null, project);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "InnerSpace project response was not readable");
                    return new(InnerSpaceStatus.Error, "InnerSpace returned a project this version cannot read.", null);
                }
            case 401:
            case 403:
                return resp.Body.Contains("errors.user.access", StringComparison.Ordinal)
                    ? new(InnerSpaceStatus.NotInnerSpaceAdmin, "The console account needs admin access to InnerSpace.", null)
                    : new(InnerSpaceStatus.NoSession, "InnerSpace did not accept the console sign-in.", null);
            case 404:
            case 502:
            case 503:
                return new(InnerSpaceStatus.NotInstalled, "InnerSpace is not installed or not running on the console.", null);
            case 0:
                return new(InnerSpaceStatus.Error, resp.Body, null);
            default:
                return new(InnerSpaceStatus.Error, $"InnerSpace answered HTTP {resp.Status}.", null);
        }
    }

    private static async Task<byte[]?> FetchBytesAsync(UniFiApiClient client, string? url, CancellationToken ct)
    {
        var path = ConsolePath(url);
        if (path == null) return null;
        var (status, bytes) = await client.GetRawBytesAsync(UniFiApiScope.Console, path, ct);
        return status == 200 && bytes.Length > 0 ? bytes : null;
    }

    /// <summary>
    /// The console-relative path of an InnerSpace asset URL, or null when it is not one. Only paths
    /// under InnerSpace's proxy are fetched, whatever the payload says.
    /// </summary>
    internal static string? ConsolePath(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Contains("://", StringComparison.Ordinal) || url.Contains("..", StringComparison.Ordinal)
            || url.Contains("%2e", StringComparison.OrdinalIgnoreCase) || url.Contains('\\'))
            return null;
        var path = url.TrimStart('/');
        return path.StartsWith("proxy/innerspace/", StringComparison.Ordinal) ? path : null;
    }

    /// <summary>The image type from its first bytes; InnerSpace stores Design Center images without an extension.</summary>
    internal static string SniffImageType(byte[] b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46
            && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "image/webp";
        var head = System.Text.Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 512)).TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
            || (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && head.Contains("<svg", StringComparison.OrdinalIgnoreCase)))
            return "image/svg+xml";
        return "application/octet-stream";
    }

    // ---- Storage ----

    private async Task SaveImageAsync(NetworkOptimizerDbContext db, FloorPlan floor, byte[] bytes, string type,
        (double SwLat, double SwLng, double NeLat, double NeLng, double RotationDeg) b, CancellationToken ct)
    {
        var image = new FloorPlanImage
        {
            FloorPlanId = floor.Id,
            Label = ImageLabel,
            SwLatitude = b.SwLat,
            SwLongitude = b.SwLng,
            NeLatitude = b.NeLat,
            NeLongitude = b.NeLng,
            RotationDeg = b.RotationDeg,
            Opacity = 0.7,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.FloorPlanImages.Add(image);
        await db.SaveChangesAsync(ct);

        var ext = type switch { "image/jpeg" => ".jpg", "image/webp" => ".webp", _ => ".png" };
        var dir = Path.Combine(FloorPlanDirectory, floor.BuildingId.ToString());
        Directory.CreateDirectory(dir);
        var fileName = $"floor_{floor.FloorNumber}_img_{image.Id}{ext}";
        await File.WriteAllBytesAsync(Path.Combine(dir, fileName), bytes, ct);
        image.ImagePath = Path.Combine(floor.BuildingId.ToString(), fileName);
        await db.SaveChangesAsync(ct);
    }

    private string FloorPlanDirectory => FloorPlanService.GetFloorPlanDirectory(_siteContext.Slug, _siteContext.IsDefault);

    private void DeleteImageFile(string? relative)
    {
        var full = FloorPlanService.ResolveInsideDirectory(FloorPlanDirectory, relative);
        if (full == null || !File.Exists(full)) return;
        try { File.Delete(full); }
        catch (IOException ex) { _logger.LogWarning(ex, "Failed to delete replaced InnerSpace floor image {Path}", full); }
    }

    private static Placement? ParsePlacement(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return new Placement(r.GetProperty("lat").GetDouble(), r.GetProperty("lng").GetDouble(), r.GetProperty("rotationDeg").GetDouble());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static int CountSegments(string? wallsJson)
    {
        if (string.IsNullOrEmpty(wallsJson)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(wallsJson);
            return doc.RootElement.EnumerateArray()
                .Sum(w => w.TryGetProperty("points", out var p) ? Math.Max(0, p.GetArrayLength() - 1) : 0);
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
