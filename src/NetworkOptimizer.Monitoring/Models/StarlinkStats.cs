namespace NetworkOptimizer.Monitoring.Models;

/// <summary>
/// Snapshot of a Starlink user terminal's health, taken from the dish's local
/// gRPC status API. Link latency/throughput are deliberately absent - the
/// Monitoring pipeline already measures RTT and WAN throughput with better
/// fidelity; this model carries what only the dish can report.
/// </summary>
public class StarlinkStats
{
    /// <summary>When these stats were collected (UTC)</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    // Device identity
    /// <summary>Terminal ID (e.g. "ut01234567-...")</summary>
    public string? DeviceId { get; set; }
    /// <summary>Hardware revision (e.g. "rev4_panda_prod1")</summary>
    public string? HardwareVersion { get; set; }
    /// <summary>Firmware version string</summary>
    public string? SoftwareVersion { get; set; }
    /// <summary>ISO country code the terminal reports</summary>
    public string? CountryCode { get; set; }
    /// <summary>Number of boots the terminal has recorded</summary>
    public int? BootCount { get; set; }
    /// <summary>Seconds since the dish last booted</summary>
    public long? UptimeSeconds { get; set; }

    // Physical link
    /// <summary>Negotiated Ethernet speed between dish and router/LAN, Mbps</summary>
    public int? EthSpeedMbps { get; set; }
    /// <summary>Instantaneous power draw in watts (heater spikes show here)</summary>
    public double? PowerInWatts { get; set; }
    /// <summary>Average power draw over the samples since the previous poll, watts</summary>
    public double? PowerInAvgWatts { get; set; }
    /// <summary>Peak power draw over the samples since the previous poll, watts</summary>
    public double? PowerInMaxWatts { get; set; }

    // Obstruction
    /// <summary>Fraction of sky time obstructed (0..1)</summary>
    public double? FractionObstructed { get; set; }
    /// <summary>Whether the dish considers itself obstructed right now</summary>
    public bool? CurrentlyObstructed { get; set; }
    /// <summary>Seconds of valid obstruction measurement backing FractionObstructed</summary>
    public double? ObstructionValidSeconds { get; set; }
    /// <summary>Average duration of prolonged obstructions, seconds (null when the dish has none to report)</summary>
    public double? AvgProlongedObstructionDurationSeconds { get; set; }

    // Signal / positioning
    /// <summary>Whether SNR is above the noise floor</summary>
    public bool? IsSnrAboveNoiseFloor { get; set; }
    /// <summary>Whether the dish reports persistently low SNR</summary>
    public bool? IsSnrPersistentlyLow { get; set; }
    /// <summary>Whether GPS has a valid fix</summary>
    public bool? GpsValid { get; set; }
    /// <summary>Number of GPS satellites in the solution</summary>
    public int? GpsSatellites { get; set; }
    /// <summary>Dish mechanical tilt from vertical, degrees</summary>
    public double? TiltAngleDeg { get; set; }
    /// <summary>Current boresight azimuth, degrees</summary>
    public double? BoresightAzimuthDeg { get; set; }
    /// <summary>Current boresight elevation, degrees</summary>
    public double? BoresightElevationDeg { get; set; }
    /// <summary>Desired boresight azimuth, degrees</summary>
    public double? DesiredBoresightAzimuthDeg { get; set; }
    /// <summary>Desired boresight elevation, degrees</summary>
    public double? DesiredBoresightElevationDeg { get; set; }
    /// <summary>Attitude estimate uncertainty, degrees</summary>
    public double? AttitudeUncertaintyDeg { get; set; }

    // Loss seen by the dish itself (to its ground station), from the 1 Hz
    // history ring buffer, aggregated over the samples since the previous poll
    /// <summary>Mean pop ping drop rate (0..1) since the previous poll</summary>
    public double? PingDropRateAvg { get; set; }
    /// <summary>Worst 1-second pop ping drop rate (0..1) since the previous poll</summary>
    public double? PingDropRateMax { get; set; }

    // Outages (from the dish's outage log)
    /// <summary>Outages recorded in the samples since the previous poll</summary>
    public int OutageCountDelta { get; set; }
    /// <summary>Total outage seconds in the samples since the previous poll</summary>
    public double OutageSecondsDelta { get; set; }
    /// <summary>Cause of the most recent outage (e.g. "NO_PINGS", "OBSTRUCTED")</summary>
    public string? LastOutageCause { get; set; }
    /// <summary>Start of the most recent outage (UTC)</summary>
    public DateTime? LastOutageAt { get; set; }
    /// <summary>Duration of the most recent outage, seconds</summary>
    public double? LastOutageDurationSeconds { get; set; }

    // Service state
    /// <summary>Names of alerts currently raised by the dish (empty when healthy)</summary>
    public List<string> ActiveAlerts { get; set; } = new();
    /// <summary>Terminal disablement code ("OKAY" when in service)</summary>
    public string? DisablementCode { get; set; }
    /// <summary>Software update state (e.g. "IDLE", "FETCHING", "REBOOT_REQUIRED")</summary>
    public string? SoftwareUpdateState { get; set; }
    /// <summary>Mobility class (e.g. "STATIONARY", "MOBILE")</summary>
    public string? MobilityClass { get; set; }
    /// <summary>Service plan class (e.g. "CONSUMER", "BUSINESS")</summary>
    public string? ClassOfService { get; set; }
    /// <summary>Why downlink bandwidth is being limited (e.g. "NO_LIMIT", "POLICY_LIMIT")</summary>
    public string? DownlinkRestrictedReason { get; set; }
    /// <summary>Why uplink bandwidth is being limited</summary>
    public string? UplinkRestrictedReason { get; set; }
    /// <summary>Hardware self-test result from diagnostics ("PASSED"/"FAILED"), null if unavailable</summary>
    public string? HardwareSelfTest { get; set; }
    /// <summary>Failing self-test codes when HardwareSelfTest is "FAILED"</summary>
    public List<string> HardwareSelfTestCodes { get; set; } = new();
}
