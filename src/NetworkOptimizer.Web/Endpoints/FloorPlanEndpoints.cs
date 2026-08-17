using ApexCharts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Audit;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web;
using NetworkOptimizer.Web.Endpoints;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Authorization;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Identity;
using NetworkOptimizer.Web.Services.CableModemProviders;
using NetworkOptimizer.Web.Services.Licensing;
using NetworkOptimizer.Web.Services.OntProviders;
using NetworkOptimizer.Web.Services.Ssh;
using NetworkOptimizer.WiFi.Models;
using Serilog;
using Serilog.Events;

namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// Buildings, floors, floor images, planned APs, the AP catalog, and heatmap generation for the Wi-Fi Optimizer's Signal Map.
/// </summary>
public static class FloorPlanEndpoints
{
    public static void Map(WebApplication app)
    {
        // Gate 2 (design doc 06): every endpoint is mapped onto a group that carries its
        // authorization policy, which is what architecture test A1 checks. Reads are any
        // authenticated user, running a test is Operator, and changes are Admin.
        var read = app.MapGroup("").RequireAuthorization(Policies.RequireViewer);
        // IFloorPlanAdminService gates every mutation on the site in context (Site Operator,
        // matching the editor's own gate), so the group carries the metadata and the service
        // carries the boundary. Install-wide Admin here blocked the very people the service admits.
        var admin = app.MapGroup("").RequireAuthorization(Policies.RequireViewer);

        // --- Building & Floor Plan API ---

        read.MapGet("/api/floor-plan/buildings", async (FloorPlanService svc) =>
        {
            var buildings = await svc.GetBuildingsAsync();
            return Results.Ok(buildings.Select(b => new
            {
                b.Id,
                b.Name,
                b.CenterLatitude,
                b.CenterLongitude,
                b.CreatedAt,
                Floors = b.Floors.Select(f => new
                {
                    f.Id,
                    f.BuildingId,
                    f.FloorNumber,
                    f.Label,
                    f.SwLatitude,
                    f.SwLongitude,
                    f.NeLatitude,
                    f.NeLongitude,
                    f.Opacity,
                    f.WallsJson,
                    f.FloorMaterial,
                    HasImage = !string.IsNullOrEmpty(f.ImagePath),
                    f.CreatedAt,
                    f.UpdatedAt
                })
            }));
        });

        admin.MapPost("/api/floor-plan/buildings", async (HttpContext context, IFloorPlanAdminService floorAdmin, FloorPlanService svc, ApMapService apMapSvc, PlannedApService plannedApSvc, HeatmapDataCache heatmapCache) =>
        {
            var request = await context.Request.ReadFromJsonAsync<BuildingRequest>();
            if (request == null) return Results.BadRequest(new { error = "Request body is required" });
            var building = await svc.CreateBuildingAsync(request.Name?.Trim() ?? "", request.CenterLatitude, request.CenterLongitude);
            await heatmapCache.InvalidateAndReloadAsync(svc, apMapSvc, plannedApSvc);
            return Results.Ok(new { building.Id, building.Name, building.CenterLatitude, building.CenterLongitude });
        });

        admin.MapPut("/api/floor-plan/buildings/{id:int}", async (int id, HttpContext context, IFloorPlanAdminService floorAdmin, FloorPlanService svc, ApMapService apMapSvc, PlannedApService plannedApSvc, HeatmapDataCache heatmapCache) =>
        {
            var request = await context.Request.ReadFromJsonAsync<BuildingRequest>();
            if (request == null) return Results.BadRequest(new { error = "Request body is required" });
            var building = await svc.UpdateBuildingAsync(id, request.Name?.Trim() ?? "", request.CenterLatitude, request.CenterLongitude);
            await heatmapCache.InvalidateAndReloadAsync(svc, apMapSvc, plannedApSvc);
            return building != null ? Results.Ok(new { success = true }) : Results.NotFound();
        });

        admin.MapDelete("/api/floor-plan/buildings/{id:int}", async (int id, IFloorPlanAdminService floorAdmin, FloorPlanService svc, ApMapService apMapSvc, PlannedApService plannedApSvc, HeatmapDataCache heatmapCache) =>
        {
            await floorAdmin.DeleteBuildingAsync(id);
            await heatmapCache.InvalidateAndReloadAsync(svc, apMapSvc, plannedApSvc);
            return Results.NoContent();
        });

        read.MapGet("/api/floor-plan/buildings/{id:int}/floors", async (int id, FloorPlanService svc) =>
        {
            var floors = await svc.GetFloorsAsync(id);
            return Results.Ok(floors.Select(f => new
            {
                f.Id,
                f.BuildingId,
                f.FloorNumber,
                f.Label,
                f.SwLatitude,
                f.SwLongitude,
                f.NeLatitude,
                f.NeLongitude,
                f.Opacity,
                f.WallsJson,
                f.FloorMaterial,
                HasImage = !string.IsNullOrEmpty(f.ImagePath),
                f.CreatedAt,
                f.UpdatedAt
            }));
        });

        admin.MapPost("/api/floor-plan/buildings/{id:int}/floors", async (int id, HttpContext context, IFloorPlanAdminService floorAdmin, FloorPlanService svc, ApMapService apMapSvc, PlannedApService plannedApSvc, HeatmapDataCache heatmapCache) =>
        {
            var request = await context.Request.ReadFromJsonAsync<FloorRequest>();
            if (request == null) return Results.BadRequest(new { error = "Request body is required" });
            var floor = await svc.CreateFloorAsync(id, request.FloorNumber, request.Label,
                request.SwLatitude, request.SwLongitude, request.NeLatitude, request.NeLongitude);
            await heatmapCache.InvalidateAndReloadAsync(svc, apMapSvc, plannedApSvc);
            return Results.Ok(new { floor.Id, floor.BuildingId, floor.FloorNumber, floor.Label });
        });

        admin.MapPut("/api/floor-plan/floors/{id:int}", async (int id, HttpContext context, IFloorPlanAdminService floorAdmin, FloorPlanService svc, ApMapService apMapSvc, PlannedApService plannedApSvc, HeatmapDataCache heatmapCache) =>
        {
            var request = await context.Request.ReadFromJsonAsync<FloorUpdateRequest>();
            if (request == null) return Results.BadRequest(new { error = "Request body is required" });
            var floor = await svc.UpdateFloorAsync(id,
                request.SwLatitude, request.SwLongitude, request.NeLatitude, request.NeLongitude,
                request.Opacity, request.WallsJson, request.Label, floorMaterial: request.FloorMaterial);
            await heatmapCache.InvalidateAndReloadAsync(svc, apMapSvc, plannedApSvc);
            return floor != null ? Results.Ok(new { success = true }) : Results.NotFound();
        });

        admin.MapDelete("/api/floor-plan/floors/{id:int}", async (int id, IFloorPlanAdminService floorAdmin, FloorPlanService svc, ApMapService apMapSvc, PlannedApService plannedApSvc, HeatmapDataCache heatmapCache) =>
        {
            await floorAdmin.DeleteFloorAsync(id);
            await heatmapCache.InvalidateAndReloadAsync(svc, apMapSvc, plannedApSvc);
            return Results.NoContent();
        });

        read.MapGet("/api/floor-plan/floors/{id:int}/image", async (int id, FloorPlanService svc) =>
        {
            var floor = await svc.GetFloorAsync(id);
            if (floor == null) return Results.NotFound();
            var imagePath = svc.GetFloorImagePath(floor);
            if (imagePath == null) return Results.NotFound();
            var mimeType = DetectImageMimeType(imagePath);
            return Results.File(imagePath, mimeType);
        });

        admin.MapPost("/api/floor-plan/floors/{id:int}/image", async (int id, HttpContext context, IFloorPlanAdminService floorAdmin, FloorPlanService svc) =>
        {
            var form = await context.Request.ReadFormAsync();
            var file = form.Files.GetFile("image");
            if (file == null || file.Length == 0)
                return Results.BadRequest(new { error = "No image file provided" });

            using var stream = file.OpenReadStream();
            await svc.SaveFloorImageAsync(id, stream);
            return Results.Ok(new { success = true });
        });

        // --- FloorPlanImage (multi-image per floor) ---

        read.MapGet("/api/floor-plan/floors/{floorId:int}/images", async (int floorId, FloorPlanService svc) =>
        {
            var images = await svc.GetFloorImagesAsync(floorId);
            return Results.Ok(images.Select(i => new
            {
                i.Id,
                i.FloorPlanId,
                i.Label,
                i.SwLatitude,
                i.SwLongitude,
                i.NeLatitude,
                i.NeLongitude,
                i.Opacity,
                i.RotationDeg,
                i.CropJson,
                i.SortOrder,
                HasFile = !string.IsNullOrEmpty(i.ImagePath)
            }));
        });

        admin.MapPost("/api/floor-plan/floors/{floorId:int}/images", async (int floorId, HttpContext context, IFloorPlanAdminService floorAdmin, FloorPlanService svc) =>
        {
            const long maxFileSize = 50 * 1024 * 1024; // 50 MB
            var form = await context.Request.ReadFormAsync();
            var file = form.Files.GetFile("image");
            if (file == null || file.Length == 0)
                return Results.BadRequest(new { error = "No image file provided" });
            if (file.Length > maxFileSize)
                return Results.BadRequest(new { error = "File exceeds 50 MB limit" });

            double.TryParse(form["swLat"], System.Globalization.CultureInfo.InvariantCulture, out var swLat);
            double.TryParse(form["swLng"], System.Globalization.CultureInfo.InvariantCulture, out var swLng);
            double.TryParse(form["neLat"], System.Globalization.CultureInfo.InvariantCulture, out var neLat);
            double.TryParse(form["neLng"], System.Globalization.CultureInfo.InvariantCulture, out var neLng);
            var label = form["label"].FirstOrDefault() ?? "";

            using var stream = file.OpenReadStream();
            var image = await svc.CreateFloorImageAsync(floorId, stream, swLat, swLng, neLat, neLng, label);
            return Results.Ok(new
            {
                image.Id,
                image.FloorPlanId,
                image.Label,
                image.SwLatitude,
                image.SwLongitude,
                image.NeLatitude,
                image.NeLongitude,
                image.Opacity,
                image.RotationDeg,
                image.CropJson,
                image.SortOrder,
                HasFile = true
            });
        });

        read.MapGet("/api/floor-plan/images/{imageId:int}/file", async (int imageId, FloorPlanService svc) =>
        {
            var image = await svc.GetFloorImageAsync(imageId);
            if (image == null) return Results.NotFound();
            var filePath = svc.GetFloorImageFilePath(image);
            if (filePath == null) return Results.NotFound();
            var mimeType = DetectImageMimeType(filePath);
            return Results.File(filePath, mimeType);
        });

        admin.MapPut("/api/floor-plan/images/{imageId:int}", async (int imageId, FloorImageUpdateRequest req, IFloorPlanAdminService floorAdmin, FloorPlanService svc) =>
        {
            var image = await svc.UpdateFloorImageAsync(imageId, req.SwLatitude, req.SwLongitude,
                req.NeLatitude, req.NeLongitude, req.Opacity, req.RotationDeg, req.CropJson, req.Label);
            if (image == null) return Results.NotFound();
            return Results.Ok(new
            {
                image.Id,
                image.FloorPlanId,
                image.Label,
                image.SwLatitude,
                image.SwLongitude,
                image.NeLatitude,
                image.NeLongitude,
                image.Opacity,
                image.RotationDeg,
                image.CropJson,
                image.SortOrder
            });
        });

        admin.MapDelete("/api/floor-plan/images/{imageId:int}", async (int imageId, IFloorPlanAdminService floorAdmin, FloorPlanService svc) =>
        {
            return await floorAdmin.DeleteFloorImageAsync(imageId) ? Results.NoContent() : Results.NotFound();
        });

        read.MapPost("/api/floor-plan/heatmap", async (HttpContext context,
            FloorPlanService floorSvc, ApMapService apMapSvc,
            PlannedApService plannedApSvc,
            NetworkOptimizer.WiFi.Services.PropagationService propagationSvc,
            HeatmapDataCache heatmapCache) =>
        {
            var request = await context.Request.ReadFromJsonAsync<NetworkOptimizer.WiFi.Models.HeatmapRequest>();
            if (request == null) return Results.BadRequest(new { error = "Request body is required" });

            if (!request.SwLat.HasValue || !request.SwLng.HasValue || !request.NeLat.HasValue || !request.NeLng.HasValue)
                return Results.BadRequest(new { error = "Viewport bounds are required" });

            var activeFloor = request.ActiveFloor;

            // Load from cache (only hits DB when data has been invalidated)
            var cached = await heatmapCache.GetOrLoadAsync(floorSvc, apMapSvc, plannedApSvc);

            // Build placed APs list from cached markers
            var bandFilter = request.Band == "2.4" ? "2.4" : request.Band == "6" ? "6" : "5";
            var placedAps = cached.ApMarkers
                .Where(a => a.Latitude.HasValue && a.Longitude.HasValue)
                .Where(a => a.Radios.Any(r => r.Band.Contains(bandFilter)))
                .Select(a =>
                {
                    var radio = a.Radios.First(r => r.Band.Contains(bandFilter));
                    return new NetworkOptimizer.WiFi.Models.PropagationAp
                    {
                        Mac = a.Mac,
                        Model = a.Model,
                        Latitude = a.Latitude!.Value,
                        Longitude = a.Longitude!.Value,
                        Floor = a.Floor ?? 1,
                        OrientationDeg = a.OrientationDeg,
                        MountType = a.MountType,
                        AntennaMode = radio.AntennaMode,
                        TxPowerDbm = radio.TxPowerDbm ?? 20,
                        AntennaGainDbi = (radio.Eirp ?? 23) - (radio.TxPowerDbm ?? 20)
                    };
                }).ToList();

            // Add planned APs to the propagation computation (unless excluded by toggle)
            if (!request.ExcludePlannedAps)
            {
                var patternLoader = context.RequestServices.GetRequiredService<NetworkOptimizer.WiFi.Data.AntennaPatternLoader>();
                foreach (var pa in cached.PlannedAps)
                {
                    var bandDefaults = NetworkOptimizer.WiFi.Data.ApModelCatalog.GetBandDefaults(pa.Model, bandFilter);
                    var (modeGain, modeMaxTx, modeDefaultTx) = NetworkOptimizer.WiFi.Data.ApModelCatalog.ResolveForMode(bandDefaults, pa.AntennaMode);
                    var txPowerStored = bandFilter switch { "2.4" => pa.TxPower24Dbm, "6" => pa.TxPower6Dbm, _ => pa.TxPower5Dbm };
                    var txPower = txPowerStored ?? modeDefaultTx;
                    var supportedBands = patternLoader.GetSupportedBands(pa.Model);
                    if (!supportedBands.Contains(bandFilter)) continue;

                    placedAps.Add(new NetworkOptimizer.WiFi.Models.PropagationAp
                    {
                        Mac = $"planned-{pa.Id}",
                        Model = pa.Model,
                        Latitude = pa.Latitude,
                        Longitude = pa.Longitude,
                        Floor = pa.Floor,
                        OrientationDeg = pa.OrientationDeg,
                        MountType = pa.MountType,
                        AntennaMode = pa.AntennaMode,
                        TxPowerDbm = txPower,
                        AntennaGainDbi = modeGain
                    });
                }
            }

            // Apply TX power overrides from simulation slider
            if (request.TxPowerOverrides is { Count: > 0 })
            {
                foreach (var ap in placedAps)
                {
                    if (request.TxPowerOverrides.TryGetValue(ap.Mac.ToLowerInvariant(), out var overridePower))
                        ap.TxPowerDbm = overridePower;
                }
            }

            // Apply antenna mode overrides from simulation toggle (also updates gain)
            if (request.AntennaModeOverrides is { Count: > 0 })
            {
                foreach (var ap in placedAps)
                {
                    if (request.AntennaModeOverrides.TryGetValue(ap.Mac.ToLowerInvariant(), out var overrideMode))
                    {
                        ap.AntennaMode = overrideMode;
                        var bd = NetworkOptimizer.WiFi.Data.ApModelCatalog.GetBandDefaults(ap.Model, bandFilter);
                        var (gain, maxTx, _) = NetworkOptimizer.WiFi.Data.ApModelCatalog.ResolveForMode(bd, overrideMode);
                        ap.AntennaGainDbi = gain;
                        ap.TxPowerDbm = Math.Min(ap.TxPowerDbm, maxTx);
                    }
                }
            }

            // Remove disabled APs from simulation
            if (request.DisabledMacs is { Count: > 0 })
            {
                var disabled = new HashSet<string>(request.DisabledMacs, StringComparer.OrdinalIgnoreCase);
                placedAps.RemoveAll(ap => disabled.Contains(ap.Mac));
            }

            var result = propagationSvc.ComputeHeatmap(
                request.SwLat.Value, request.SwLng.Value, request.NeLat.Value, request.NeLng.Value,
                request.Band, placedAps, cached.WallsByFloor, activeFloor, request.GridResolutionMeters, cached.BuildingFloorInfos);

            // Apply calibration adjustment from real-world signal measurements if provided.
            // Filter to measurements matching the active heatmap band.
            if (request.SignalMeasurements is { Count: > 0 })
            {
                var bandFiltered = request.SignalMeasurements
                    .Where(m => RadioBandExtensions.MatchesPropagationBand(m.Band, request.Band))
                    .ToList();
                if (bandFiltered.Count > 0)
                    propagationSvc.AdjustWithMeasurements(result, bandFiltered, placedAps);
            }

            return Results.Ok(result);
        });

        // ── Planned APs ─────────────────────────────────────────────────────

        read.MapGet("/api/floor-plan/planned-aps", async (PlannedApService svc) =>
        {
            var aps = await svc.GetAllAsync();
            return Results.Ok(aps);
        });

        admin.MapPost("/api/floor-plan/planned-aps", async (HttpContext context, FloorPlanService floorSvc, ApMapService apMapSvc, PlannedApService svc, HeatmapDataCache heatmapCache) =>
        {
            var ap = await context.Request.ReadFromJsonAsync<NetworkOptimizer.Storage.Models.PlannedAp>();
            if (ap == null) return Results.BadRequest(new { error = "Request body is required" });
            var created = await svc.CreateAsync(ap);
            await heatmapCache.InvalidateAndReloadAsync(floorSvc, apMapSvc, svc);
            return Results.Ok(created);
        });

        admin.MapPut("/api/floor-plan/planned-aps/{id:int}", async (int id, HttpContext context, FloorPlanService floorSvc, ApMapService apMapSvc, PlannedApService svc, HeatmapDataCache heatmapCache) =>
        {
            var body = await context.Request.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>();
            if (body == null) return Results.BadRequest(new { error = "Request body is required" });

            if (body.TryGetValue("latitude", out var lat) && body.TryGetValue("longitude", out var lng))
                await svc.UpdateLocationAsync(id, lat.GetDouble(), lng.GetDouble());
            if (body.TryGetValue("floor", out var floor))
                await svc.UpdateFloorAsync(id, floor.GetInt32());
            if (body.TryGetValue("orientationDeg", out var deg))
                await svc.UpdateOrientationAsync(id, deg.GetInt32());
            if (body.TryGetValue("mountType", out var mt))
                await svc.UpdateMountTypeAsync(id, mt.GetString() ?? "ceiling");
            if (body.TryGetValue("txPowerDbm", out var tx) && body.TryGetValue("band", out var band))
                await svc.UpdateTxPowerAsync(id, band.GetString() ?? "5", tx.ValueKind == System.Text.Json.JsonValueKind.Null ? null : tx.GetInt32());
            if (body.TryGetValue("antennaMode", out var am))
                await svc.UpdateAntennaModeAsync(id, am.ValueKind == System.Text.Json.JsonValueKind.Null ? null : am.GetString());
            if (body.TryGetValue("name", out var name))
                await svc.UpdateNameAsync(id, (name.GetString() ?? "").Trim());

            await heatmapCache.InvalidateAndReloadAsync(floorSvc, apMapSvc, svc);
            return Results.Ok(new { success = true });
        });

        admin.MapDelete("/api/floor-plan/planned-aps/{id:int}", async (int id, FloorPlanService floorSvc, ApMapService apMapSvc, PlannedApService svc, HeatmapDataCache heatmapCache) =>
        {
            var deleted = await svc.DeleteAsync(id);
            await heatmapCache.InvalidateAndReloadAsync(floorSvc, apMapSvc, svc);
            return deleted ? Results.Ok(new { success = true }) : Results.NotFound();
        });

        read.MapGet("/api/floor-plan/ap-catalog", (NetworkOptimizer.WiFi.Data.AntennaPatternLoader patternLoader) =>
        {
            var catalog = NetworkOptimizer.WiFi.Data.ApModelCatalog.BuildCatalog(patternLoader);
            return Results.Ok(catalog.Select(c => new
            {
                model = c.Model,
                bands = c.Bands.ToDictionary(b => b.Key, b => new
                {
                    defaultTxPowerDbm = b.Value.DefaultTxPowerDbm,
                    minTxPowerDbm = b.Value.MinTxPowerDbm,
                    maxTxPowerDbm = b.Value.MaxTxPowerDbm,
                    antennaGainDbi = b.Value.AntennaGainDbi,
                    modeOverrides = b.Value.ModeOverrides?.ToDictionary(m => m.Key, m => new
                    {
                        antennaGainDbi = m.Value.AntennaGainDbi,
                        maxTxPowerDbm = m.Value.MaxTxPowerDbm,
                        defaultTxPowerDbm = m.Value.DefaultTxPowerDbm,
                    })
                }),
                defaultMountType = c.DefaultMountType,
                hasOmniVariant = c.HasOmniVariant,
                antennaVariants = c.AntennaVariants,
                iconPath = NetworkOptimizer.Web.Components.Shared.DeviceIcon.GetIconPath(c.Model) ?? "/images/devices/default-ap.png"
            }));
        });
    }

    /// <summary>
    /// Content type of a stored floor plan image, sniffed from its magic bytes so a renamed or
    /// extension-less upload is still served correctly.
    /// </summary>
    private static string DetectImageMimeType(string filePath)
    {
        try
        {
            var header = new byte[12];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(header, 0, header.Length);
            if (bytesRead >= 4)
            {
                // PNG: 89 50 4E 47
                if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
                    return "image/png";
                // JPEG: FF D8 FF
                if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
                    return "image/jpeg";
                // WebP: RIFF + 4 byte size + WEBP
                if (bytesRead >= 12 && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46
                    && header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
                    return "image/webp";
            }
        }
        catch { /* fall through */ }

        // Fallback by extension
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "image/png"
        };
    }
}
