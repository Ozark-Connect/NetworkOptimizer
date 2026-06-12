using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

public enum SnmpVersionSetting
{
    V2c = 1,
    V3 = 3
}

public enum SnmpDetectionState
{
    NotChecked,
    Disabled,
    EnabledV2c,
    EnabledV3Only,
    PollFailing,
    Working
}

public enum AccessTechnology
{
    Unknown = 0,
    Gpon = 1,
    XgsPon = 2,
    Docsis = 3,
    PppoE = 4,
    DirectEthernet = 5,
    FixedWireless = 6,
    Satellite = 7,
    Cellular = 8,
    Other = 9,
    Dsl = 10
}

public class MonitoringSettings
{
    [Key]
    public int Id { get; set; }

    public bool Enabled { get; set; }

    // InfluxDB connection
    [MaxLength(500)]
    public string InfluxDbUrl { get; set; } = "http://localhost:8086";

    [MaxLength(500)]
    public string? InfluxDbToken { get; set; }

    [MaxLength(100)]
    public string InfluxDbOrg { get; set; } = "network-optimizer";

    [MaxLength(100)]
    public string InfluxDbBucket { get; set; } = "network_monitoring";

    [MaxLength(100)]
    public string InfluxDbLongtermBucket { get; set; } = "network_monitoring_longterm";

    // Polling intervals (seconds)
    public int FastPollIntervalSeconds { get; set; } = 5;
    public int MediumPollIntervalSeconds { get; set; } = 30;
    public int SlowPollIntervalSeconds { get; set; } = 300;

    // SNMP — populated by auto-detection from UniFi settings API
    public SnmpVersionSetting SnmpVersion { get; set; } = SnmpVersionSetting.V2c;

    [MaxLength(500)]
    public string? SnmpCommunity { get; set; }

    [MaxLength(200)]
    public string? SnmpV3Username { get; set; }

    [MaxLength(500)]
    public string? SnmpV3AuthPassword { get; set; }

    public SnmpDetectionState SnmpDetectionState { get; set; } = SnmpDetectionState.NotChecked;
    public DateTime? LastSnmpDetection { get; set; }
    public DateTime? LastSnmpSuccess { get; set; }

    // Access ISP context (set by the upstream wizard, used for first-mile labelling)
    public AccessTechnology AccessTechnology { get; set; } = AccessTechnology.Unknown;

    [MaxLength(50)]
    public string? WanNeighborMac { get; set; }

    [MaxLength(200)]
    public string? WanNeighborOui { get; set; }

    // Upstream tracer state. LastUpstreamDiscoveryAt is set on commit. The auto
    // re-discovery scheduler re-runs discovery every 7 days and flips
    // UpstreamDiscoveryNeedsReview = true when it finds a different set of
    // candidates than what's currently committed. UI shows a banner; user confirms.
    public DateTime? LastUpstreamDiscoveryAt { get; set; }
    public bool UpstreamDiscoveryNeedsReview { get; set; }

    // InfluxDB health
    public bool? InfluxDbReachable { get; set; }
    public DateTime? LastInfluxDbCheck { get; set; }

    [MaxLength(500)]
    public string? LastInfluxDbError { get; set; }

    public bool Flex25GLatencyMigrated { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool HasSnmpCredentials => !string.IsNullOrEmpty(SnmpCommunity) || !string.IsNullOrEmpty(SnmpV3Username);
}
