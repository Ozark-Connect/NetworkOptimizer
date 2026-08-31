using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Models;

/// <summary>
/// Identified client device information from UniFi controller.
/// </summary>
/// <summary>
/// What the switch or gateway port a wired client is plugged into reports about itself.
///
/// Directions are stated from the CLIENT's point of view, not the port's: a port receives what the
/// client uploads, so the port's inbound counters are the client's upload. Callers render these as
/// they read them.
///
/// These are the port's counters, not the client's. Anything else behind the same port - an
/// unmanaged switch, a daisy chain - is counted here too.
/// </summary>
public class WiredPortStats
{
    public string? SwitchName { get; set; }
    public int? Port { get; set; }
    public bool? LinkUp { get; set; }
    public long? LinkSpeedBps { get; set; }

    public double? DownloadBps { get; set; }
    public double? UploadBps { get; set; }

    public long? ErrorsToClient { get; set; }
    public long? ErrorsFromClient { get; set; }
    public long? DropsToClient { get; set; }
    public long? DropsFromClient { get; set; }

    public long? PacketsToClient { get; set; }
    public long? PacketsFromClient { get; set; }

    /// <summary>When the port was last polled, so a stale reading can say so.</summary>
    public DateTime? At { get; set; }
}

/// <summary>One throughput reading, as download and upload from the client's point of view.</summary>
public record ThroughputSample(DateTime Time, double? DownloadBps, double? UploadBps);

/// <summary>Bytes a client moved in one bucket, as its download and upload.</summary>
public record UsageBucket(DateTime Time, long DownloadBytes, long UploadBytes);

/// <summary>
/// One application's share of a client's WAN traffic, as UniFi Network identified it.
/// <paramref name="Note"/> explains a name that is not an application's: traffic UniFi Network
/// could not identify, or an application our catalog has no name for.
/// </summary>
public record AppUsageRow(string Name, string Category, string? IconDomain, string? IconClass, long DownloadBytes, long UploadBytes, long ActivitySeconds, string? Note = null)
{
    public long TotalBytes => DownloadBytes + UploadBytes;
}

/// <summary>
/// A client's data usage over a window. WAN and LAN answer different questions and are never added:
/// WAN is what left the site, LAN includes traffic that never did.
/// </summary>
public class ClientDataUsage
{
    /// <summary>The window actually covered; "All" is capped to what the sources keep.</summary>
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public TimeSpan Bucket { get; set; }

    /// <summary>Per client, from UniFi Network's own reports.</summary>
    public IReadOnlyList<UsageBucket> Wan { get; set; } = Array.Empty<UsageBucket>();

    /// <summary>From our own counters: the switch port's for a wired client, the access point's for wireless.</summary>
    public IReadOnlyList<UsageBucket> Lan { get; set; } = Array.Empty<UsageBucket>();

    /// <summary>The LAN figure is a switch port's total, so anything else behind that port is in it.</summary>
    public bool LanIsPortTotal { get; set; }

    public long WanDownloadBytes => Wan.Sum(b => b.DownloadBytes);
    public long WanUploadBytes => Wan.Sum(b => b.UploadBytes);
    public long LanDownloadBytes => Lan.Sum(b => b.DownloadBytes);
    public long LanUploadBytes => Lan.Sum(b => b.UploadBytes);
}

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
    public int? ChannelWidth { get; set; }
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

    // AP lock
    public bool FixedApEnabled { get; set; }
    public string? FixedApMac { get; set; }
    public string? FixedApName { get; set; }

    // Wired uplink: which switch or gateway port this client is plugged into
    public string? SwitchMac { get; set; }
    public string? SwitchName { get; set; }
    public int? SwitchPort { get; set; }

    // Device metadata
    public string? Oui { get; set; }
    public string? NetworkName { get; set; }
    public string? Essid { get; set; }
    public int? Satisfaction { get; set; }

    /// <summary>True when identified from client history (device not currently connected)</summary>
    public bool IsOffline { get; set; }

    /// <summary>True when signal data was sourced from the WiFiman realtime endpoint</summary>
    public bool HasWiFiManData { get; set; }

    /// <summary>
    /// True when signal data came from the access point's own AP Agent rather than the console.
    /// Optional accelerator: false is the normal state and means the WiFiman path is in use.
    /// </summary>
    public bool HasApAgentData { get; set; }

    /// <summary>
    /// VPN hop type when this client connects through Tailscale, Teleport, or a UniFi
    /// remote-user VPN. Set only for the simplified VPN dashboard view; null otherwise.
    /// </summary>
    public HopType? VpnType { get; set; }

    /// <summary>True when this is a VPN-sourced client (renders the simplified dashboard view)</summary>
    public bool IsVpn => VpnType != null;

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
    public string? ApMac { get; set; }
    public string? ApName { get; set; }
    public string? ClientMac { get; set; }
    public string? ClientIp { get; set; }
    public string? DeviceName { get; set; }
}
