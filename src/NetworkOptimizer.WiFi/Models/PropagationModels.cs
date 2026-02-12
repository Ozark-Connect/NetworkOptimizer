namespace NetworkOptimizer.WiFi.Models;

/// <summary>
/// Request for RF propagation heatmap computation.
/// </summary>
public class HeatmapRequest
{
    /// <summary>Floor plan ID to compute heatmap for</summary>
    public int FloorId { get; set; }

    /// <summary>RF band: "2.4", "5", or "6"</summary>
    public string Band { get; set; } = "5";

    /// <summary>Grid resolution in meters (default 1m)</summary>
    public double GridResolutionMeters { get; set; } = 1.0;

    /// <summary>Viewport bounds from the map (if set, overrides floor plan bounds)</summary>
    public double? SwLat { get; set; }
    public double? SwLng { get; set; }
    public double? NeLat { get; set; }
    public double? NeLng { get; set; }
}

/// <summary>
/// Response containing computed RF propagation heatmap data.
/// </summary>
public class HeatmapResponse
{
    /// <summary>Grid width in cells</summary>
    public int Width { get; set; }

    /// <summary>Grid height in cells</summary>
    public int Height { get; set; }

    /// <summary>Southwest corner latitude</summary>
    public double SwLat { get; set; }

    /// <summary>Southwest corner longitude</summary>
    public double SwLng { get; set; }

    /// <summary>Northeast corner latitude</summary>
    public double NeLat { get; set; }

    /// <summary>Northeast corner longitude</summary>
    public double NeLng { get; set; }

    /// <summary>Flat array of signal strength values in dBm (row-major, SW corner is [0,0])</summary>
    public float[] Data { get; set; } = Array.Empty<float>();
}

/// <summary>
/// An AP positioned on a floor for propagation computation.
/// </summary>
public class PropagationAp
{
    public string Mac { get; set; } = "";
    public string Model { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public int Floor { get; set; } = 1;
    public int TxPowerDbm { get; set; } = 20;
    public int AntennaGainDbi { get; set; } = 3;
    public int OrientationDeg { get; set; }
    public string MountType { get; set; } = "ceiling";

    /// <summary>
    /// Active antenna mode (e.g., "OMNI", "Internal"). Null for standard indoor APs.
    /// Used to select variant antenna pattern (e.g., "U7-Outdoor:omni").
    /// </summary>
    public string? AntennaMode { get; set; }
}

/// <summary>
/// A wall segment for propagation computation.
/// </summary>
public class PropagationWall
{
    public List<LatLng> Points { get; set; } = new();
    public string Material { get; set; } = "drywall";

    /// <summary>
    /// Per-segment materials. Materials[i] is the material for the segment between
    /// Points[i] and Points[i+1]. If null, all segments use <see cref="Material"/>.
    /// </summary>
    public List<string>? Materials { get; set; }
}

/// <summary>
/// A latitude/longitude coordinate pair.
/// </summary>
public class LatLng
{
    public double Lat { get; set; }
    public double Lng { get; set; }
}

/// <summary>
/// Antenna pattern data for a single AP model and band.
/// </summary>
public class AntennaPattern
{
    /// <summary>360 gain values indexed by azimuth angle (0-359 degrees)</summary>
    public float[] Azimuth { get; set; } = Array.Empty<float>();

    /// <summary>359 gain values indexed by elevation angle (0-358 degrees)</summary>
    public float[] Elevation { get; set; } = Array.Empty<float>();
}
