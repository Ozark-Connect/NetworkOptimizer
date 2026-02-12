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
        List<PropagationWall> walls,
        int activeFloor,
        double gridResolutionMeters = 1.0)
    {
        var freqMhz = MaterialAttenuation.GetCenterFrequencyMhz(band);

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

        // Pre-compute wall segments as line segments for ray-casting
        var wallSegments = PrecomputeWallSegments(walls);

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
                        ap, pointLat, pointLng, activeFloor, band, freqMhz, wallSegments);

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
        List<WallSegment> wallSegments)
    {
        // 2D distance from AP to point
        var distance2d = HaversineDistance(ap.Latitude, ap.Longitude, pointLat, pointLng);
        if (distance2d < 0.1) distance2d = 0.1; // avoid log(0)

        // Floor separation
        var floorSeparation = Math.Abs(ap.Floor - activeFloor);
        var floorLoss = 0.0;
        if (floorSeparation > 0)
        {
            // Each floor crossing adds attenuation (use concrete floor by default)
            floorLoss = floorSeparation * MaterialAttenuation.GetAttenuation("floor_concrete", band);
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

        // Elevation angle (90 = horizon for same floor, decreasing for below)
        int elevationDeg;
        if (floorSeparation == 0)
        {
            elevationDeg = 90; // horizon
        }
        else
        {
            // Angle from vertical: 0 = straight down, 90 = horizon
            elevationDeg = (int)(Math.Atan2(distance2d, verticalDistance) * 180.0 / Math.PI);
            elevationDeg = Math.Clamp(elevationDeg, 0, 358);
        }

        // Apply mount type elevation offset before antenna pattern lookup.
        // The offset is the difference between the actual mount and the pattern's native orientation.
        // Outdoor APs in omni mode have patterns measured wall-mounted, but directional (non-omni)
        // patterns are measured flat (ceiling orientation), so we adjust accordingly.
        var patternNativeMount = GetPatternNativeMount(ap.Model, ap.AntennaMode);
        var patternMountOffset = patternNativeMount switch { "wall" => -90, "desktop" => 180, _ => 0 };
        var actualMountOffset = ap.MountType switch { "wall" => -90, "desktop" => 180, _ => 0 };
        var elevationOffset = actualMountOffset - patternMountOffset;
        elevationDeg = ((elevationDeg + elevationOffset) % 359 + 359) % 359;

        // Antenna pattern gain using pattern multiplication:
        // Combine 2D azimuth and elevation cuts into 3D approximation.
        // Both patterns are normalized to 0 dB at peak, so addition in dB = multiplication in linear.
        var azGain = _antennaLoader.GetAzimuthGain(ap.Model, band, azimuthDeg, ap.AntennaMode);
        var elGain = _antennaLoader.GetElevationGain(ap.Model, band, elevationDeg, ap.AntennaMode);
        var antennaGain = azGain + elGain;

        // Wall attenuation via ray-casting (only same-floor walls)
        var wallLoss = 0.0;
        if (floorSeparation == 0)
        {
            wallLoss = ComputeWallLoss(ap.Latitude, ap.Longitude, pointLat, pointLng, band, wallSegments);
        }

        // Signal = TX power + antenna gain - FSPL - wall loss - floor loss
        var signal = ap.TxPowerDbm + ap.AntennaGainDbi + antennaGain - fspl - wallLoss - floorLoss;

        return (float)signal;
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
                var material = wall.Materials != null && i < wall.Materials.Count
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
    /// APs with switchable antenna modes (those with an omni variant in the pattern
    /// data) have their directional patterns measured flat (ceiling orientation),
    /// while their omni patterns are measured wall-mounted.
    /// </summary>
    private string GetPatternNativeMount(string model, string? antennaMode)
    {
        var isOmni = !string.IsNullOrEmpty(antennaMode) &&
                     antennaMode.Equals("OMNI", StringComparison.OrdinalIgnoreCase);

        if (!isOmni && _antennaLoader.HasOmniVariant(model))
            return "ceiling";

        return MountTypeHelper.GetDefaultMountType(model);
    }

    private struct WallSegment
    {
        public double Lat1, Lng1, Lat2, Lng2;
        public string Material;
    }
}
