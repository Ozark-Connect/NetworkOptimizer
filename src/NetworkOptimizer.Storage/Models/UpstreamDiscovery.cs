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
    AccessHop = 7
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

    public UpstreamRole Role { get; set; }

    [MaxLength(50)]
    public string? WanInterface { get; set; }

    public DateTime LastValidated { get; set; } = DateTime.UtcNow;

    public DateTime LastTracerouteAt { get; set; } = DateTime.UtcNow;

    public bool IsActive { get; set; } = true;
}
