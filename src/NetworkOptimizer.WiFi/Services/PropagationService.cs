using Microsoft.Extensions.Logging;
using NetworkOptimizer.WiFi.Data;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Services;

/// <summary>
/// Computes RF signal propagation heatmaps using ITU-R P.1238 indoor path loss,
/// wall attenuation, antenna patterns, and multi-floor support.
/// </summary>
public class PropagationService
{
    private readonly AntennaPatternLoader _antennaLoader;
    private readonly ILogger<PropagationService> _logger;

    private const double EarthRadiusMeters = 6371000.0;
    private const double DefaultFloorHeightMeters = 3.0;

    // ITU-R P.1238 indoor path loss exponent (2.8 for residential/office at 5 GHz)
    private const double IndoorPathLossExponent = 2.8;

    private bool _loggedPatternInfo;

    public PropagationService(AntennaPatternLoader antennaLoader, ILogger<PropagationService> logger)
    {
        _antennaLoader = antennaLoader;
        _logger = logger;
    }

    /// <summary>
    /// Compute RF propagation heatmap for a floor plan area.
    /// </summary>
    public HeatmapResponse ComputeHeatmap(
        double swLat, double swLng, double neLat, double neLng,
        string band,
        List<PropagationAp> aps,
        Dictionary<int, List<PropagationWall>> wallsByFloor,
        int activeFloor,
        double gridResolutionMeters = 1.0,
        List<BuildingFloorInfo>? buildings = null)
    {
        var freqMhz = MaterialAttenuation.GetCenterFrequencyMhz(band);

        // Log building floor info
        if (buildings != null)
        {
            foreach (var b in buildings)
            {
                var mats = string.Join(", ", b.FloorMaterials.Select(kv => $"F{kv.Key}={kv.Value}"));
                _logger.LogInformation("Heatmap building: bounds=({SwLat},{SwLng})-({NeLat},{NeLng}) floors=[{Mats}]",
                    b.SwLat, b.SwLng, b.NeLat, b.NeLng, mats);
            }
        }

        // Log AP and antenna pattern info on first computation
        if (!_loggedPatternInfo)
        {
            _loggedPatternInfo = true;
            foreach (var ap in aps)
            {
                var pattern = _antennaLoader.GetPattern(ap.Model, band, ap.AntennaMode);
                _logger.LogInformation(
                    "Heatmap AP: {Model} band={Band} txPower={TxPower}dBm antennaGain={AntennaGain}dBi antennaMode={Mode} pattern={HasPattern}",
                    ap.Model, band, ap.TxPowerDbm, ap.AntennaGainDbi, ap.AntennaMode ?? "default", pattern != null);
            }
        }

        // Calculate grid dimensions
        var widthMeters = HaversineDistance(swLat, swLng, swLat, neLng);
        var heightMeters = HaversineDistance(swLat, swLng, neLat, swLng);

        var gridWidth = Math.Max(1, (int)(widthMeters / gridResolutionMeters));
        var gridHeight = Math.Max(1, (int)(heightMeters / gridResolutionMeters));

        // Cap grid size to prevent memory/CPU issues
        if (gridWidth > 500) gridWidth = 500;
        if (gridHeight > 500) gridHeight = 500;

        var data = new float[gridWidth * gridHeight];

        var latStep = (neLat - swLat) / gridHeight;
        var lngStep = (neLng - swLng) / gridWidth;

        // Pre-compute wall segments per floor for ray-casting
        var segmentsByFloor = new Dictionary<int, List<WallSegment>>();
        foreach (var (floor, floorWalls) in wallsByFloor)
        {
            segmentsByFloor[floor] = PrecomputeWallSegments(floorWalls);
        }

        for (int y = 0; y < gridHeight; y++)
        {
            var pointLat = swLat + (y + 0.5) * latStep;
            for (int x = 0; x < gridWidth; x++)
            {
                var pointLng = swLng + (x + 0.5) * lngStep;
                var bestSignal = float.MinValue;

                foreach (var ap in aps)
                {
                    var signal = ComputeSignalAtPoint(
                        ap, pointLat, pointLng, activeFloor, band, freqMhz, segmentsByFloor, buildings);

                    if (signal > bestSignal)
                        bestSignal = signal;
                }

                data[y * gridWidth + x] = aps.Count > 0 ? bestSignal : -100f;
            }
        }

        return new HeatmapResponse
        {
            Width = gridWidth,
            Height = gridHeight,
            SwLat = swLat,
            SwLng = swLng,
            NeLat = neLat,
            NeLng = neLng,
            Data = data
        };
    }

    private float ComputeSignalAtPoint(
        PropagationAp ap,
        double pointLat, double pointLng,
        int activeFloor,
        string band, double freqMhz,
        Dictionary<int, List<WallSegment>> segmentsByFloor,
        List<BuildingFloorInfo>? buildings)
    {
        // 2D distance from AP to point
        var distance2d = HaversineDistance(ap.Latitude, ap.Longitude, pointLat, pointLng);
        if (distance2d < 0.1) distance2d = 0.1; // avoid log(0)

        // Floor separation
        var floorSeparation = Math.Abs(ap.Floor - activeFloor);
        var floorLoss = 0.0;
        if (floorSeparation > 0)
        {
            floorLoss = ComputeFloorLoss(ap, pointLat, pointLng, activeFloor, band, buildings);
        }

        // 3D distance including floor separation
        var verticalDistance = floorSeparation * DefaultFloorHeightMeters;
        var distance3d = Math.Sqrt(distance2d * distance2d + verticalDistance * verticalDistance);
        if (distance3d < 0.1) distance3d = 0.1;

        // Indoor path loss (ITU-R P.1238): uses higher exponent than free-space for realistic indoor falloff
        var fspl = 10 * IndoorPathLossExponent * Math.Log10(distance3d) + 20 * Math.Log10(freqMhz) - 27.55;

        // Azimuth angle from AP to point, adjusted for AP orientation
        var azimuth = CalculateBearing(ap.Latitude, ap.Longitude, pointLat, pointLng);
        var azimuthDeg = (int)((azimuth - ap.OrientationDeg + 360) % 360);

        // Elevation angle in pattern coordinates (ceiling mount native):
        // 0 = straight down, 90 = horizon, 180 = straight up
        // IW and Wall APs on a "desktop" stand sit upright (same as wall-mounted)
        var effectiveMount = ap.MountType == "desktop" && IsStandMountModel(ap.Model) ? "wall" : ap.MountType;
        int elevationDeg;
        if (floorSeparation == 0)
        {
            elevationDeg = 90; // horizon - no mount offset on same floor
        }
        else
        {
            // angleFromVertical: 0 = straight down/up, 90 = horizon
            var angleFromVertical = (int)(Math.Atan2(distance2d, verticalDistance) * 180.0 / Math.PI);
            // Target below AP → looking down (near 0), target above → looking up (near 180)
            var targetAbove = activeFloor > ap.Floor;
            elevationDeg = targetAbove ? 180 - angleFromVertical : angleFromVertical;
            elevationDeg = Math.Clamp(elevationDeg, 0, 358);

            // Apply mount type elevation offset for cross-floor only.
            // Same floor uses horizon (90) regardless of mount since we don't model within-floor height.
            var patternNativeMount = GetPatternNativeMount(ap.Model, band, ap.AntennaMode);
            var patternMountOffset = patternNativeMount switch { "wall" => -90, "desktop" => 180, _ => 0 };
            var actualMountOffset = effectiveMount switch { "wall" => -90, "desktop" => 180, _ => 0 };
            var elevationOffset = actualMountOffset - patternMountOffset;
            elevationDeg = ((elevationDeg + elevationOffset) % 359 + 359) % 359;
        }

        // Antenna pattern gain using pattern multiplication:
        // Combine 2D azimuth and elevation cuts into 3D approximation.
        // Both patterns are normalized to 0 dB at peak, so addition in dB = multiplication in linear.
        //
        // Wall mount swap: when an AP is wall-mounted, its elevation plane (vertical for ceiling)
        // rotates to horizontal, and its azimuth plane (horizontal for ceiling) rotates to vertical.
        // So horizontal directionality comes from the elevation pattern, and vertical from azimuth.
        float azGain, elGain;
        if (effectiveMount == "wall")
        {
            azGain = _antennaLoader.GetElevationGain(ap.Model, band, azimuthDeg, ap.AntennaMode);
            elGain = _antennaLoader.GetAzimuthGain(ap.Model, band, elevationDeg, ap.AntennaMode);
        }
        else
        {
            azGain = _antennaLoader.GetAzimuthGain(ap.Model, band, azimuthDeg, ap.AntennaMode);
            elGain = _antennaLoader.GetElevationGain(ap.Model, band, elevationDeg, ap.AntennaMode);
        }
        var antennaGain = azGain + elGain;

        // Wall attenuation via ray-casting
        // For same-floor: check active floor walls
        // For cross-floor: check both AP's floor walls and active floor walls
        // (signal must pass through walls on AP's floor before going through the floor,
        //  then through walls on the active floor to reach the observation point)
        var wallLoss = 0.0;
        if (segmentsByFloor.TryGetValue(activeFloor, out var activeFloorSegments))
        {
            wallLoss += ComputeWallLoss(ap.Latitude, ap.Longitude, pointLat, pointLng, band, activeFloorSegments);
        }
        if (floorSeparation > 0 && segmentsByFloor.TryGetValue(ap.Floor, out var apFloorSegments))
        {
            wallLoss += ComputeWallLoss(ap.Latitude, ap.Longitude, pointLat, pointLng, band, apFloorSegments);
        }

        // Signal = TX power + antenna gain - FSPL - wall loss - floor loss
        var signal = ap.TxPowerDbm + ap.AntennaGainDbi + antennaGain - fspl - wallLoss - floorLoss;

        return (float)signal;
    }

    /// <summary>
    /// Compute floor attenuation between AP and active floor.
    /// Uses the observation point's building materials when available (the point is on
    /// the target floor, so that building's slab is the physical barrier). Falls back
    /// to the AP's building, then to wood frame default.
    /// Each crossed floor uses the upper floor's material (floor N+1's slab separates N from N+1).
    /// </summary>
    private static double ComputeFloorLoss(
        PropagationAp ap, double pointLat, double pointLng,
        int activeFloor, string band, List<BuildingFloorInfo>? buildings)
    {
        if (buildings == null || buildings.Count == 0)
        {
            return Math.Abs(ap.Floor - activeFloor) * MaterialAttenuation.GetAttenuation("floor_wood", band);
        }

        // Find building containing the observation point (primary) or the AP (fallback).
        // When multiple buildings overlap, pick the smallest area (most specific match)
        // to avoid a large-bounds single-floor building shadowing a smaller multi-floor one.
        var pointBuilding = FindSmallestContainingBuilding(buildings, pointLat, pointLng);
        var apBuilding = FindSmallestContainingBuilding(buildings, ap.Latitude, ap.Longitude);

        var building = pointBuilding ?? apBuilding;

        if (building == null)
        {
            // Both AP and point are outdoors
            return 0.0;
        }

        // Sum attenuation for each floor crossed between AP floor and active floor.
        // The physical barrier between floor N and N+1 is the slab at floor N+1,
        // so use the upper floor's material for each crossing.
        var totalLoss = 0.0;
        var minFloor = Math.Min(ap.Floor, activeFloor);
        var maxFloor = Math.Max(ap.Floor, activeFloor);

        for (var f = minFloor + 1; f <= maxFloor; f++)
        {
            var material = building.FloorMaterials.GetValueOrDefault(f, "floor_wood");
            totalLoss += MaterialAttenuation.GetAttenuation(material, band);
        }

        return totalLoss;
    }

    private double ComputeWallLoss(
        double apLat, double apLng,
        double pointLat, double pointLng,
        string band,
        List<WallSegment> wallSegments)
    {
        var totalLoss = 0.0;

        foreach (var wall in wallSegments)
        {
            if (LineSegmentsIntersect(
                apLat, apLng, pointLat, pointLng,
                wall.Lat1, wall.Lng1, wall.Lat2, wall.Lng2))
            {
                totalLoss += MaterialAttenuation.GetAttenuation(wall.Material, band);
            }
        }

        return totalLoss;
    }

    private List<WallSegment> PrecomputeWallSegments(List<PropagationWall> walls)
    {
        var segments = new List<WallSegment>();
        foreach (var wall in walls)
        {
            for (int i = 0; i < wall.Points.Count - 1; i++)
            {
                var material = wall.Materials != null && i < wall.Materials.Count && wall.Materials[i] != null
                    ? wall.Materials[i]
                    : wall.Material;

                segments.Add(new WallSegment
                {
                    Lat1 = wall.Points[i].Lat,
                    Lng1 = wall.Points[i].Lng,
                    Lat2 = wall.Points[i + 1].Lat,
                    Lng2 = wall.Points[i + 1].Lng,
                    Material = material
                });
            }
        }
        return segments;
    }

    /// <summary>
    /// Haversine distance in meters between two lat/lng points.
    /// </summary>
    public static double HaversineDistance(double lat1, double lng1, double lat2, double lng2)
    {
        var dLat = (lat2 - lat1) * Math.PI / 180.0;
        var dLng = (lng2 - lng1) * Math.PI / 180.0;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusMeters * c;
    }

    /// <summary>
    /// Calculate bearing (compass direction) from point 1 to point 2 in degrees.
    /// </summary>
    private static double CalculateBearing(double lat1, double lng1, double lat2, double lng2)
    {
        var dLng = (lng2 - lng1) * Math.PI / 180.0;
        var lat1Rad = lat1 * Math.PI / 180.0;
        var lat2Rad = lat2 * Math.PI / 180.0;

        var x = Math.Sin(dLng) * Math.Cos(lat2Rad);
        var y = Math.Cos(lat1Rad) * Math.Sin(lat2Rad) -
                Math.Sin(lat1Rad) * Math.Cos(lat2Rad) * Math.Cos(dLng);

        return (Math.Atan2(x, y) * 180.0 / Math.PI + 360) % 360;
    }

    /// <summary>
    /// Test if two 2D line segments intersect using cross-product method.
    /// </summary>
    private static bool LineSegmentsIntersect(
        double ax1, double ay1, double ax2, double ay2,
        double bx1, double by1, double bx2, double by2)
    {
        var d1 = CrossProduct(bx1, by1, bx2, by2, ax1, ay1);
        var d2 = CrossProduct(bx1, by1, bx2, by2, ax2, ay2);
        var d3 = CrossProduct(ax1, ay1, ax2, ay2, bx1, by1);
        var d4 = CrossProduct(ax1, ay1, ax2, ay2, bx2, by2);

        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
            ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
        {
            return true;
        }

        return false;
    }

    private static double CrossProduct(double ax, double ay, double bx, double by, double cx, double cy)
    {
        return (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
    }

    /// <summary>
    /// Determine the native mount orientation of the antenna pattern data.
    /// All directional patterns (base, narrow, wide, panel) are measured flat (ceiling).
    /// Only omni patterns are measured wall-mounted.
    /// When the requested omni variant doesn't exist for the band (e.g., U7-Pro-Outdoor
    /// omni on 6 GHz), the pattern loader falls back to the base directional pattern,
    /// so we must also fall back to ceiling.
    /// </summary>
    private string GetPatternNativeMount(string model, string band, string? antennaMode)
    {
        var isOmni = !string.IsNullOrEmpty(antennaMode) &&
                     antennaMode.Equals("OMNI", StringComparison.OrdinalIgnoreCase);

        if (isOmni && _antennaLoader.HasOmniVariant(model))
        {
            // Check if the omni variant actually has this band. If not, the pattern
            // loader fell back to the base directional pattern, so use ceiling mount.
            var omniPattern = _antennaLoader.GetPattern(model, band, "OMNI");
            var basePattern = _antennaLoader.GetPattern(model, band);
            if (omniPattern != null && omniPattern != basePattern)
                return "wall"; // true omni pattern loaded, measured wall-mounted
        }

        return "ceiling"; // all directional patterns are measured flat
    }

    /// <summary>
    /// IW and Wall APs can sit on a desk stand in the same upright orientation as wall-mounted.
    /// "Desktop" for these models means wall orientation, not flipped like ceiling-mount APs.
    /// </summary>
    private static bool IsStandMountModel(string model)
    {
        var m = model.EndsWith("-B", StringComparison.OrdinalIgnoreCase) ? model[..^2] : model;
        return m.Contains("-IW", StringComparison.OrdinalIgnoreCase) ||
               m.Contains("-Wall", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Find the building with the smallest bounding area that contains the given point.
    /// Prevents large-bounds buildings from shadowing smaller, more specific ones.
    /// </summary>
    private static BuildingFloorInfo? FindSmallestContainingBuilding(List<BuildingFloorInfo> buildings, double lat, double lng)
    {
        BuildingFloorInfo? best = null;
        var bestArea = double.MaxValue;
        foreach (var b in buildings)
        {
            if (lat >= b.SwLat && lat <= b.NeLat && lng >= b.SwLng && lng <= b.NeLng)
            {
                var area = (b.NeLat - b.SwLat) * (b.NeLng - b.SwLng);
                if (area < bestArea)
                {
                    bestArea = area;
                    best = b;
                }
            }
        }
        return best;
    }

    private struct WallSegment
    {
        public double Lat1, Lng1, Lat2, Lng2;
        public string Material;
    }
}
