using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

public enum InterfaceDirection
{
    Unknown = 0,
    Uplink = 1,
    Downlink = 2,
    Wan = 3
}

public class InterfaceNameMap
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(50)]
    public string DeviceMac { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string IfName { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? FriendlyName { get; set; }

    public int? PortNumber { get; set; }

    public InterfaceDirection Direction { get; set; } = InterfaceDirection.Unknown;

    public bool IsWan { get; set; }

    [MaxLength(100)]
    public string? WanName { get; set; }

    public int? IfIndex { get; set; }

    [MaxLength(255)]
    public string? IfAlias { get; set; }

    public int? SpeedMbps { get; set; }

    /// <summary>
    /// True when the UniFi PortTable reports an SFP module in this port, false for
    /// RJ45 copper, null when the media type is unknown (e.g. gateway interfaces
    /// with no matching PortTable entry).
    /// </summary>
    public bool? IsSfp { get; set; }

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
