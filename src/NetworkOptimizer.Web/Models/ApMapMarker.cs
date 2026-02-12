namespace NetworkOptimizer.Web.Models;

/// <summary>
/// View model combining AP snapshot data with saved map location.
/// Used by SpeedTestMap to render AP markers on the coverage map.
/// </summary>
public class ApMapMarker
{
    /// <summary>AP MAC address</summary>
    public string Mac { get; set; } = "";

    /// <summary>User-assigned AP name</summary>
    public string Name { get; set; } = "";

    /// <summary>Device model (e.g., "U7-Pro")</summary>
    public string Model { get; set; } = "";

    /// <summary>Saved latitude (null if not yet placed on map)</summary>
    public double? Latitude { get; set; }

    /// <summary>Saved longitude (null if not yet placed on map)</summary>
    public double? Longitude { get; set; }

    /// <summary>Floor number (null if not placed, default 1 for single-story)</summary>
    public int? Floor { get; set; }

    /// <summary>AP orientation in degrees (0-359, 0 = North, clockwise)</summary>
    public int OrientationDeg { get; set; }

    /// <summary>Mount type: "ceiling", "wall", or "desktop"</summary>
    public string MountType { get; set; } = "ceiling";

    /// <summary>Whether the AP is currently online</summary>
    public bool IsOnline { get; set; }

    /// <summary>Total connected clients across all radios</summary>
    public int TotalClients { get; set; }

    /// <summary>Per-radio summary for popup display</summary>
    public List<ApRadioSummary> Radios { get; set; } = new();
}

/// <summary>
/// Summary of a single radio on an AP for map popup display
/// </summary>
public class ApRadioSummary
{
    /// <summary>Band display string (e.g., "2.4 GHz", "5 GHz", "6 GHz")</summary>
    public string Band { get; set; } = "";

    /// <summary>UniFi radio code for CSS badge class (e.g., "ng", "na", "6e")</summary>
    public string RadioCode { get; set; } = "";

    /// <summary>Current channel number</summary>
    public int? Channel { get; set; }

    /// <summary>Channel width in MHz</summary>
    public int? ChannelWidth { get; set; }

    /// <summary>TX power in dBm</summary>
    public int? TxPowerDbm { get; set; }

    /// <summary>Minimum TX power in dBm (device capability)</summary>
    public int? MinTxPowerDbm { get; set; }

    /// <summary>Maximum TX power in dBm (device capability)</summary>
    public int? MaxTxPowerDbm { get; set; }

    /// <summary>EIRP (Effective Isotropic Radiated Power) in dBm</summary>
    public int? Eirp { get; set; }

    /// <summary>Number of connected clients on this radio</summary>
    public int? Clients { get; set; }

    /// <summary>Channel utilization percentage (0-100)</summary>
    public int? Utilization { get; set; }

    /// <summary>
    /// Active antenna mode name (e.g., "Internal", "OMNI").
    /// Null for indoor APs with no switchable modes.
    /// </summary>
    public string? AntennaMode { get; set; }
}
