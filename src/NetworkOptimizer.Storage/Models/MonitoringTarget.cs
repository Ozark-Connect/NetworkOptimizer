using System.ComponentModel.DataAnnotations;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Storage.Models;

public enum MonitoringTargetType
{
    Fabric = 0,
    Wan = 1,
    AccessIsp = 2,
    Transit = 3,
    Custom = 4
}

public enum DiscoveryMethod
{
    DirectRouter = 0,
    PathProxy = 1
}

public class MonitoringTarget
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string TargetId { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string Address { get; set; } = string.Empty;

    public ProbeMode ProbeMode { get; set; } = ProbeMode.Icmp;

    public int? Port { get; set; }

    public MonitoringTargetType TargetType { get; set; }

    [MaxLength(50)]
    public string? DeviceMac { get; set; }

    public int? AsnNumber { get; set; }

    [MaxLength(200)]
    public string? AsnName { get; set; }

    [Required, MaxLength(100)]
    public string VantagePoint { get; set; } = "server";

    public int PollIntervalSeconds { get; set; } = 10;

    public int PingCount { get; set; } = 10;

    public bool Enabled { get; set; } = true;

    public bool AutoDiscovered { get; set; }

    public DiscoveryMethod? DiscoveryMethod { get; set; }

    [MaxLength(255)]
    public string? PtrHostname { get; set; }

    [MaxLength(200)]
    public string? AutoLabel { get; set; }

    public DateTime? LastVerified { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
