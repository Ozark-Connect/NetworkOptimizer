using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Per-WAN state captured by the upstream tracer wizard. One row per WAN interface
/// (wan1 / wan2 / starlink / etc.) — replaces the single set of global columns on
/// MonitoringSettings (WanNeighborOui, AccessTechnology, LastUpstreamDiscoveryAt,
/// UpstreamDiscoveryNeedsReview) which couldn't represent multi-WAN setups.
///
/// Survives the WAN interface being temporarily disabled. Rows are upserted on
/// each tracer commit; deletion only happens if the user explicitly removes the
/// WAN from UniFi and we garbage-collect it.
/// </summary>
public class WanDiscoveryContext
{
    [Key, MaxLength(50)]
    public string WanInterface { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? L2NeighborMac { get; set; }

    [MaxLength(200)]
    public string? L2NeighborOui { get; set; }

    [MaxLength(50)]
    public string? L2NeighborIp { get; set; }

    public AccessTechnology AccessTechnology { get; set; } = AccessTechnology.Unknown;

    public DateTime? LastDiscoveryAt { get; set; }
    public bool NeedsReview { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
