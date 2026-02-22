using NetworkOptimizer.Web.Models;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Caches pre-loaded heatmap data (buildings, walls, APs) to avoid re-querying
/// the database on every pan/zoom. Invalidated when underlying data changes.
/// </summary>
public sealed class HeatmapDataCache
{
    private volatile CachedData? _cached;
    private volatile int _version;

    public void Invalidate() => Interlocked.Increment(ref _version);

    /// <summary>
    /// Invalidate and eagerly reload the cache so the next heatmap request is instant.
    /// </summary>
    public async Task InvalidateAndReloadAsync(
        FloorPlanService floorSvc,
        ApMapService apMapSvc,
        PlannedApService plannedApSvc)
    {
        Invalidate();
        await GetOrLoadAsync(floorSvc, apMapSvc, plannedApSvc);
    }

    public async Task<CachedData> GetOrLoadAsync(
        FloorPlanService floorSvc,
        ApMapService apMapSvc,
        PlannedApService plannedApSvc)
    {
        var current = _cached;
        if (current != null && current.Version == _version)
            return current;

        // Reload all data
        var snapshotVersion = _version;

        var allBuildings = await floorSvc.GetBuildingsAsync();
        var apMarkers = await apMapSvc.GetApMapMarkersAsync();
        var plannedAps = await plannedApSvc.GetAllAsync();

        // Pre-parse walls from JSON (avoid re-deserializing on every request)
        var wallsByFloor = new Dictionary<int, List<PropagationWall>>();
        foreach (var building in allBuildings)
        {
            foreach (var f in building.Floors)
            {
                if (string.IsNullOrEmpty(f.WallsJson)) continue;
                try
                {
                    var floorWalls = System.Text.Json.JsonSerializer.Deserialize<List<PropagationWall>>(
                        f.WallsJson,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (floorWalls != null)
                    {
                        if (!wallsByFloor.ContainsKey(f.FloorNumber))
                            wallsByFloor[f.FloorNumber] = new List<PropagationWall>();
                        wallsByFloor[f.FloorNumber].AddRange(floorWalls);
                    }
                }
                catch { /* ignore bad JSON */ }
            }
        }

        var buildingFloorInfos = allBuildings.Select(building =>
        {
            var floors = building.Floors;
            if (floors.Count == 0) return null;
            return new BuildingFloorInfo
            {
                SwLat = floors.Min(f => f.SwLatitude),
                SwLng = floors.Min(f => f.SwLongitude),
                NeLat = floors.Max(f => f.NeLatitude),
                NeLng = floors.Max(f => f.NeLongitude),
                FloorMaterials = floors.ToDictionary(f => f.FloorNumber, f => f.FloorMaterial)
            };
        }).OfType<BuildingFloorInfo>().ToList();

        var data = new CachedData(snapshotVersion, allBuildings, wallsByFloor, apMarkers, plannedAps, buildingFloorInfos);
        _cached = data;
        return data;
    }

    public sealed record CachedData(
        int Version,
        List<NetworkOptimizer.Storage.Models.Building> Buildings,
        Dictionary<int, List<PropagationWall>> WallsByFloor,
        List<ApMapMarker> ApMarkers,
        List<NetworkOptimizer.Storage.Models.PlannedAp> PlannedAps,
        List<BuildingFloorInfo> BuildingFloorInfos);
}
