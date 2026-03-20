using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Persisted WAN steering traffic class rule.
/// Defines match criteria and target WAN for load-balanced traffic steering on the gateway.
/// </summary>
public class WanSteerTrafficClass
{
    [Key]
    public int Id { get; set; }

    /// <summary>Friendly name for this rule (e.g., "Steam Downloads")</summary>
    [MaxLength(100)]
    public string Name { get; set; } = "";

    /// <summary>Whether this rule is active</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Order in which rules are evaluated (lower = first)</summary>
    public int SortOrder { get; set; }

    /// <summary>Target WAN network group key (e.g., "WAN", "WAN2")</summary>
    [MaxLength(50)]
    public string TargetWanKey { get; set; } = "";

    /// <summary>Probability 0.0-1.0 for xt_statistic load balancing (1.0 = all matching traffic)</summary>
    public double Probability { get; set; } = 1.0;

    /// <summary>Source CIDRs as JSON array (e.g., ["192.168.1.0/24"]). Null = match all sources.</summary>
    public string? SrcCidrsJson { get; set; }

    /// <summary>Source MACs as JSON array (e.g., ["aa:bb:cc:dd:ee:ff"]). Null = match all MACs.</summary>
    public string? SrcMacsJson { get; set; }

    /// <summary>Destination CIDRs as JSON array (e.g., ["162.254.192.0/21"]). Null = match all destinations.</summary>
    public string? DstCidrsJson { get; set; }

    /// <summary>Protocol filter: "tcp", "udp", or null for any protocol.</summary>
    [MaxLength(10)]
    public string? Protocol { get; set; }

    /// <summary>Source ports as JSON array (e.g., ["1234", "5000:5100"]). Null = match all source ports.</summary>
    public string? SrcPortsJson { get; set; }

    /// <summary>Destination ports as JSON array (e.g., ["443", "27015:27030"]). Null = match all dest ports.</summary>
    public string? DstPortsJson { get; set; }
}
