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
        {
            // Version matches, but AP radio config may have changed in UniFi
            // (e.g., antenna mode, TX power). Check if propagation-relevant fields
            // are still current by fetching fresh AP data and comparing fingerprints.
            var freshMarkers = await apMapSvc.GetApMapMarkersAsync();
            var freshFingerprint = ComputeRadioFingerprint(freshMarkers);
            if (freshFingerprint == current.RadioFingerprint)
                return current;

            // Radio config changed - rebuild cache with fresh AP data
            current = current with { ApMarkers = freshMarkers, RadioFingerprint = freshFingerprint };
            _cached = current;
            return current;
        }

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

        var radioFingerprint = ComputeRadioFingerprint(apMarkers);
        var data = new CachedData(snapshotVersion, allBuildings, wallsByFloor, apMarkers, plannedAps, buildingFloorInfos, radioFingerprint);
        _cached = data;
        return data;
    }

    /// <summary>
    /// Compute a fingerprint of propagation-relevant AP radio fields.
    /// Changes to antenna mode, TX power, or channel trigger a heatmap recompute.
    /// </summary>
    private static string ComputeRadioFingerprint(List<ApMapMarker> markers)
    {
        // Build a stable string from propagation-relevant fields, sorted by MAC for consistency
        var parts = markers
            .OrderBy(m => m.Mac, StringComparer.OrdinalIgnoreCase)
            .Select(m =>
            {
                var radios = string.Join("|", m.Radios
                    .OrderBy(r => r.Band)
                    .Select(r => $"{r.Band}:{r.TxPowerDbm}:{r.AntennaMode}:{r.Channel}:{r.ChannelWidth}"));
                return $"{m.Mac}={radios}";
            });
        return string.Join(";", parts);
    }

    public sealed record CachedData(
        int Version,
        List<NetworkOptimizer.Storage.Models.Building> Buildings,
        Dictionary<int, List<PropagationWall>> WallsByFloor,
        List<ApMapMarker> ApMarkers,
        List<NetworkOptimizer.Storage.Models.PlannedAp> PlannedAps,
        List<BuildingFloorInfo> BuildingFloorInfos,
        string RadioFingerprint = "");
}
