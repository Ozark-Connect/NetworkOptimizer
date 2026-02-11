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
}

/// <summary>
/// A wall segment for propagation computation.
/// </summary>
public class PropagationWall
{
    public List<LatLng> Points { get; set; } = new();
    public string Material { get; set; } = "drywall";
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
