using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Models;

/// <summary>
/// Identified client device information from UniFi controller.
/// </summary>
public class ClientIdentity
{
    public string Mac { get; set; } = "";
    public string? Name { get; set; }
    public string? Hostname { get; set; }
    public string? Ip { get; set; }
    public bool IsWired { get; set; }

    // Wi-Fi signal
    public int? SignalDbm { get; set; }
    public int? NoiseDbm { get; set; }
    public int? Channel { get; set; }
    public string? Band { get; set; }
    public string? Protocol { get; set; }
    public long? TxRateKbps { get; set; }
    public long? RxRateKbps { get; set; }
    public bool IsMlo { get; set; }
    public List<MloLinkDetail>? MloLinks { get; set; }

    // Connected AP info
    public string? ApMac { get; set; }
    public string? ApName { get; set; }
    public string? ApModel { get; set; }
    public int? ApChannel { get; set; }
    public int? ApTxPower { get; set; }
    public int? ApEirp { get; set; }
    public int? ApClientCount { get; set; }
    public string? ApRadioBand { get; set; }

    // Device metadata
    public string? Oui { get; set; }
    public string? NetworkName { get; set; }
    public string? Essid { get; set; }
    public int? Satisfaction { get; set; }

    /// <summary>True when identified from client history (device not currently connected)</summary>
    public bool IsOffline { get; set; }

    /// <summary>Best display name (Name > Hostname > MAC)</summary>
    public string DisplayName => !string.IsNullOrEmpty(Name) ? Name
        : !string.IsNullOrEmpty(Hostname) ? Hostname
        : Mac;

    /// <summary>Formatted band for display (2.4 GHz, 5 GHz, 6 GHz)</summary>
    public string? BandDisplay => Band switch
    {
        "ng" => "2.4 GHz",
        "na" => "5 GHz",
        "6e" => "6 GHz",
        _ => Band
    };
}

/// <summary>
/// Result of a signal poll cycle, combining live client data with trace analysis.
/// </summary>
public class SignalPollResult
{
    public ClientIdentity Client { get; set; } = new();
    public PathAnalysisResult? PathAnalysis { get; set; }
    public string? TraceHash { get; set; }
    public bool TraceChanged { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// GPS coordinates submitted from browser geolocation.
/// </summary>
public class GpsUpdateRequest
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public int? AccuracyMeters { get; set; }
}

/// <summary>
/// Source of signal data (local polling vs UniFi controller history).
/// </summary>
public enum SignalDataSource
{
    Local,
    UniFiController
}

/// <summary>
/// Signal log entry for history display.
/// </summary>
public class SignalHistoryEntry
{
    public DateTime Timestamp { get; set; }
    public int? SignalDbm { get; set; }
    public int? NoiseDbm { get; set; }
    public int? Channel { get; set; }
    public int? ChannelWidth { get; set; }
    public string? Band { get; set; }
    public string? Protocol { get; set; }
    public long? TxRateKbps { get; set; }
    public long? RxRateKbps { get; set; }
    public string? ApMac { get; set; }
    public string? ApName { get; set; }
    public int? HopCount { get; set; }
    public double? BottleneckLinkSpeedMbps { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public SignalDataSource DataSource { get; set; } = SignalDataSource.Local;
}

/// <summary>
/// Trace change event for trace history display.
/// </summary>
public class TraceChangeEntry
{
    public DateTime Timestamp { get; set; }
    public string? TraceHash { get; set; }
    public string? TraceJson { get; set; }
    public int? HopCount { get; set; }
    public double? BottleneckLinkSpeedMbps { get; set; }
    public PathAnalysisResult? PathAnalysis { get; set; }
}

/// <summary>
/// A GPS-located signal measurement point for display on the floor plan map.
/// </summary>
public class SignalMapPoint
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public int SignalDbm { get; set; }
    public DateTime Timestamp { get; set; }
    public string? Band { get; set; }
    public int? Channel { get; set; }
    public string? ApName { get; set; }
    public string? ClientMac { get; set; }
    public string? ClientIp { get; set; }
    public string? DeviceName { get; set; }
}
