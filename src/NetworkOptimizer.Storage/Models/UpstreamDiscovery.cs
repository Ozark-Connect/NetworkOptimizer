using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

public enum UpstreamRole
{
    Olt = 0,
    Cmts = 1,
    Bng = 2,
    Aggregation = 3,
    Border = 4,
    Transit = 5,
    PathProxy = 6,
    AccessHop = 7,
    AccessGateway = 8
}

public class UpstreamDiscovery
{
    [Key]
    public int Id { get; set; }

    public int? MonitoringTargetId { get; set; }

    public int AsnNumber { get; set; }

    [MaxLength(200)]
    public string? AsnName { get; set; }

    [Required, MaxLength(50)]
    public string HopIp { get; set; } = string.Empty;

    public int HopNumber { get; set; }

    /// <summary>
    /// Space-separated IPs of the monitored hops that appear before this one on any
    /// discovery trace it was seen on - its proven upstream ancestors. ISP Health uses
    /// these to confirm one hop genuinely routes through another (a witness only absolves
    /// a hop in its ancestor set), correct across divergent paths. Empty for a first hop.
    /// </summary>
    [MaxLength(2000)]
    public string? AncestorHopIps { get; set; }

    public UpstreamRole Role { get; set; }

    [MaxLength(50)]
    public string? WanInterface { get; set; }

    public DateTime LastValidated { get; set; } = DateTime.UtcNow;

    public DateTime LastTracerouteAt { get; set; } = DateTime.UtcNow;

    public bool IsActive { get; set; } = true;
}
