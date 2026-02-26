namespace NetworkOptimizer.Sqm.Models;

/// <summary>
/// Configuration for SQM (Smart Queue Management) on a WAN interface
/// </summary>
public class SqmConfiguration
{
    /// <summary>
    /// Connection type (determines speed assumptions and tuning)
    /// </summary>
    public ConnectionType ConnectionType { get; set; } = ConnectionType.DocsisCable;

    /// <summary>
    /// Friendly name for this connection (e.g., "Yelcot", "Starlink")
    /// </summary>
    public string ConnectionName { get; set; } = "";

    /// <summary>
    /// Advertised/nominal download speed in Mbps (what customer pays for)
    /// </summary>
    public int NominalDownloadSpeed { get; set; } = 300;

    /// <summary>
    /// Advertised/nominal upload speed in Mbps
    /// </summary>
    public int NominalUploadSpeed { get; set; } = 35;

    /// <summary>
    /// WAN interface name (e.g., "eth2", "eth4")
    /// </summary>
    public string Interface { get; set; } = "eth2";

    /// <summary>
    /// IFB (Intermediate Functional Block) device for traffic shaping
    /// </summary>
    public string IfbDevice => $"ifb{Interface}";

    /// <summary>
    /// Maximum download speed in Mbps (ceiling)
    /// </summary>
    public int MaxDownloadSpeed { get; set; } = 285;

    /// <summary>
    /// Minimum download speed floor in Mbps
    /// </summary>
    public int MinDownloadSpeed { get; set; } = 190;

    /// <summary>
    /// Absolute maximum achievable download speed in Mbps
    /// </summary>
    public int AbsoluteMaxDownloadSpeed { get; set; } = 280;

    /// <summary>
    /// Overhead multiplier for speedtest results (1.05 = 5% overhead)
    /// </summary>
    public double OverheadMultiplier { get; set; } = 1.05;

    /// <summary>
    /// Ping target host for latency monitoring
    /// </summary>
    public string PingHost { get; set; } = "40.134.217.121";

    /// <summary>
    /// Baseline latency in milliseconds (unloaded optimal ping)
    /// </summary>
    public double BaselineLatency { get; set; } = 17.9;

    /// <summary>
    /// Latency threshold in milliseconds (trigger adjustment when exceeded)
    /// </summary>
    public double LatencyThreshold { get; set; } = 2.2;

    /// <summary>
    /// Rate decrease multiplier when high latency detected (0.97 = 3% decrease per deviation)
    /// </summary>
    public double LatencyDecrease { get; set; } = 0.97;

    /// <summary>
    /// Rate increase multiplier when latency normalizes (1.04 = 4% increase)
    /// </summary>
    public double LatencyIncrease { get; set; } = 1.04;

    /// <summary>
    /// InfluxDB endpoint for metrics collection (optional)
    /// </summary>
    public string? InfluxDbEndpoint { get; set; }

    /// <summary>
    /// InfluxDB token for authentication (optional)
    /// </summary>
    public string? InfluxDbToken { get; set; }

    /// <summary>
    /// InfluxDB organization (optional)
    /// </summary>
    public string? InfluxDbOrg { get; set; }

    /// <summary>
    /// InfluxDB bucket name (optional)
    /// </summary>
    public string? InfluxDbBucket { get; set; }

    /// <summary>
    /// Speedtest schedule (cron format) - default: 6 AM and 6:30 PM
    /// </summary>
    public List<string> SpeedtestSchedule { get; set; } = new() { "0 6 * * *", "30 18 * * *" };

    /// <summary>
    /// Ping adjustment interval in minutes (default: 5)
    /// </summary>
    public int PingAdjustmentInterval { get; set; } = 5;

    /// <summary>
    /// Learning mode enabled - collect baseline data without aggressive adjustments
    /// </summary>
    public bool LearningMode { get; set; } = false;

    /// <summary>
    /// Learning mode start timestamp
    /// </summary>
    public DateTime? LearningModeStarted { get; set; }

    /// <summary>
    /// Optional preferred speedtest server ID
    /// </summary>
    public string? PreferredSpeedtestServerId { get; set; }

    /// <summary>
    /// Baseline blending weight when within 10% threshold (baseline portion)
    /// </summary>
    public double BlendingWeightWithin { get; set; } = 0.60;

    /// <summary>
    /// Baseline blending weight when below 10% threshold (baseline portion)
    /// </summary>
    public double BlendingWeightBelow { get; set; } = 0.80;

    /// <summary>
    /// Get the ConnectionProfile for this configuration
    /// </summary>
    public ConnectionProfile GetProfile()
    {
        return new ConnectionProfile
        {
            Type = ConnectionType,
            Name = ConnectionName,
            Interface = Interface,
            NominalDownloadMbps = NominalDownloadSpeed,
            NominalUploadMbps = NominalUploadSpeed,
            PingHost = PingHost,
            PreferredSpeedtestServerId = PreferredSpeedtestServerId
        };
    }

    /// <summary>
    /// Apply connection profile settings to calculate optimal parameters
    /// based on connection type and nominal speed
    /// </summary>
    public void ApplyProfileSettings(int? wanLinkSpeedMbps = null)
    {
        var profile = new ConnectionProfile
        {
            Type = ConnectionType,
            Name = ConnectionName,
            Interface = Interface,
            NominalDownloadMbps = NominalDownloadSpeed,
            NominalUploadMbps = NominalUploadSpeed,
            PingHost = PingHost,
            PreferredSpeedtestServerId = PreferredSpeedtestServerId
        };

        // Apply calculated values from profile
        MaxDownloadSpeed = profile.MaxDownloadMbps;
        MinDownloadSpeed = profile.MinDownloadMbps;
        AbsoluteMaxDownloadSpeed = profile.AbsoluteMaxDownloadMbps;
        OverheadMultiplier = profile.OverheadMultiplier;
        BaselineLatency = profile.BaselineLatency;
        LatencyThreshold = profile.LatencyThreshold;
        LatencyDecrease = profile.LatencyDecrease;
        LatencyIncrease = profile.LatencyIncrease;

        // Cap at physical WAN link speed if known (e.g., 1000 for 1GbE)
        if (wanLinkSpeedMbps is > 0)
        {
            MaxDownloadSpeed = Math.Min(MaxDownloadSpeed, wanLinkSpeedMbps.Value);
            AbsoluteMaxDownloadSpeed = Math.Min(AbsoluteMaxDownloadSpeed, wanLinkSpeedMbps.Value);
        }

        // Apply blending ratios
        var (withinWeight, _) = profile.GetBlendingRatios(withinThreshold: true);
        var (belowWeight, _) = profile.GetBlendingRatios(withinThreshold: false);
        BlendingWeightWithin = withinWeight;
        BlendingWeightBelow = belowWeight;

        // Apply default speedtest server if not specified
        PreferredSpeedtestServerId ??= profile.PreferredSpeedtestServerId;
    }

    /// <summary>
    /// Create a configuration from a ConnectionProfile
    /// </summary>
    public static SqmConfiguration FromProfile(ConnectionProfile profile)
    {
        var (withinWeight, _) = profile.GetBlendingRatios(withinThreshold: true);
        var (belowWeight, _) = profile.GetBlendingRatios(withinThreshold: false);

        return new SqmConfiguration
        {
            ConnectionType = profile.Type,
            ConnectionName = profile.Name,
            Interface = profile.Interface,
            NominalDownloadSpeed = profile.NominalDownloadMbps,
            NominalUploadSpeed = profile.NominalUploadMbps,
            MaxDownloadSpeed = profile.MaxDownloadMbps,
            MinDownloadSpeed = profile.MinDownloadMbps,
            AbsoluteMaxDownloadSpeed = profile.AbsoluteMaxDownloadMbps,
            OverheadMultiplier = profile.OverheadMultiplier,
            PingHost = profile.PingHost,
            BaselineLatency = profile.BaselineLatency,
            LatencyThreshold = profile.LatencyThreshold,
            LatencyDecrease = profile.LatencyDecrease,
            LatencyIncrease = profile.LatencyIncrease,
            PreferredSpeedtestServerId = profile.PreferredSpeedtestServerId,
            BlendingWeightWithin = withinWeight,
            BlendingWeightBelow = belowWeight
        };
    }

    /// <summary>
    /// Get a summary of the calculated SQM parameters
    /// </summary>
    public string GetParameterSummary()
    {
        return $"""
            Connection: {ConnectionProfile.GetConnectionTypeName(ConnectionType)} ({ConnectionName})
            Interface: {Interface} (IFB: {IfbDevice})
            Nominal Speed: {NominalDownloadSpeed}/{NominalUploadSpeed} Mbps (down/up)
            Speed Range: {MinDownloadSpeed}-{MaxDownloadSpeed} Mbps (floor-ceiling)
            Absolute Max: {AbsoluteMaxDownloadSpeed} Mbps
            Overhead: {(OverheadMultiplier - 1) * 100:F0}%
            Latency: {BaselineLatency}ms baseline, {LatencyThreshold}ms threshold
            Rate Adjust: -{(1 - LatencyDecrease) * 100:F0}% / +{(LatencyIncrease - 1) * 100:F0}%
            """;
    }
}
