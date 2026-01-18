using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Persisted SQM WAN configuration for deployment.
/// Stores connection type, nominal speeds, and other settings so they
/// can be adjusted and redeployed without re-entering everything.
/// </summary>
public class SqmWanConfiguration
{
    [Key]
    public int Id { get; set; }

    /// <summary>Site this WAN configuration belongs to</summary>
    public int SiteId { get; set; }

    /// <summary>Navigation property to parent site</summary>
    public Site? Site { get; set; }

    /// <summary>WAN identifier (1 or 2)</summary>
    public int WanNumber { get; set; }

    /// <summary>Whether this WAN is enabled for SQM</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Connection type as int (maps to NetworkOptimizer.Sqm.Models.ConnectionType enum)</summary>
    public int ConnectionType { get; set; } = 0;

    /// <summary>Friendly name for this connection (e.g., "Yelcot", "Starlink")</summary>
    [MaxLength(100)]
    public string Name { get; set; } = "";

    /// <summary>WAN interface name (e.g., "eth2", "eth0")</summary>
    [MaxLength(50)]
    public string Interface { get; set; } = "";

    /// <summary>Advertised/nominal download speed in Mbps</summary>
    public int NominalDownloadMbps { get; set; } = 300;

    /// <summary>Advertised/nominal upload speed in Mbps</summary>
    public int NominalUploadMbps { get; set; } = 35;

    /// <summary>Ping target host for latency monitoring</summary>
    [MaxLength(255)]
    public string PingHost { get; set; } = "1.1.1.1";

    /// <summary>Optional preferred Ookla speedtest server ID</summary>
    [MaxLength(50)]
    public string? SpeedtestServerId { get; set; }

    /// <summary>Morning speedtest hour (0-23), default 6 for WAN1, 5 for WAN2</summary>
    public int SpeedtestMorningHour { get; set; } = 6;

    /// <summary>Morning speedtest minute (0-59), default 0</summary>
    public int SpeedtestMorningMinute { get; set; } = 0;

    /// <summary>Evening speedtest hour (0-23), default 18</summary>
    public int SpeedtestEveningHour { get; set; } = 18;

    /// <summary>Evening speedtest minute (0-59), default 30 for WAN1, 0 for WAN2</summary>
    public int SpeedtestEveningMinute { get; set; } = 30;

    /// <summary>When this configuration was created</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When this configuration was last updated</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
