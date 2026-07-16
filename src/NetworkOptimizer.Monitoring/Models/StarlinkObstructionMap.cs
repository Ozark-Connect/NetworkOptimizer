namespace NetworkOptimizer.Monitoring.Models;

/// <summary>
/// The dish's obstruction sky map: a square grid of per-patch SNR samples in
/// the dish reference frame. Values are 0..1 SNR quality, or -1 for patches
/// the dish has not yet measured. Rendered client-side as the familiar
/// sky-dome view; never persisted to time series.
/// </summary>
public class StarlinkObstructionMap
{
    /// <summary>When the map was fetched (UTC)</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>Grid rows</summary>
    public int NumRows { get; set; }

    /// <summary>Grid columns</summary>
    public int NumCols { get; set; }

    /// <summary>Row-major SNR samples, length NumRows * NumCols; -1 = unmeasured</summary>
    public float[] Snr { get; set; } = Array.Empty<float>();

    /// <summary>Angular radius of the map from boresight, degrees</summary>
    public double MaxThetaDeg { get; set; }

    /// <summary>Reference frame the map is expressed in (e.g. "FRAME_UT")</summary>
    public string? ReferenceFrame { get; set; }
}
