using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// User-defined note for a UPnP or static port forward mapping.
/// Keyed by host IP, port, and protocol to persist across rule changes.
/// </summary>
public class UpnpNote
{
    [Key]
    public int Id { get; set; }

    /// <summary>Site this UPnP note belongs to</summary>
    public int SiteId { get; set; }

    /// <summary>Navigation property to parent site</summary>
    public Site? Site { get; set; }

    /// <summary>
    /// Internal host IP address (the forwarded-to address)
    /// </summary>
    [Required]
    [MaxLength(45)]
    public string HostIp { get; set; } = "";

    /// <summary>
    /// External destination port (can be a range like "19132-19133" or single port)
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Port { get; set; } = "";

    /// <summary>
    /// Protocol (tcp, udp, or tcp_udp)
    /// </summary>
    [Required]
    [MaxLength(10)]
    public string Protocol { get; set; } = "";

    /// <summary>
    /// User's note about this mapping
    /// </summary>
    [MaxLength(500)]
    public string? Note { get; set; }

    /// <summary>
    /// When the note was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the note was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
