using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Registry entry for an on-site agent. Lives only in the main (registry)
/// database. Secrets are never stored: the one-time enrollment token and the
/// long-lived agent key are kept as SHA-256 hashes, with the raw values shown
/// or returned exactly once.
/// </summary>
public class SiteAgent
{
    [Key]
    public int Id { get; set; }

    /// <summary>Registry id of the site this agent belongs to.</summary>
    public int SiteId { get; set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = "";

    /// <summary>SHA-256 hex of the one-time enrollment token.</summary>
    [MaxLength(64)]
    public string? EnrollmentTokenHash { get; set; }

    /// <summary>When the current enrollment token was generated.</summary>
    public DateTime? TokenCreatedAt { get; set; }

    /// <summary>SHA-256 hex of the agent key issued at enrollment.</summary>
    [MaxLength(64)]
    public string? AgentKeyHash { get; set; }

    /// <summary>When the agent exchanged its token for a key.</summary>
    public DateTime? EnrolledAt { get; set; }

    /// <summary>Last heartbeat received from the agent.</summary>
    public DateTime? LastSeenAt { get; set; }

    /// <summary>Agent software version reported on the last heartbeat.</summary>
    [MaxLength(32)]
    public string? LastVersion { get; set; }

    /// <summary>
    /// The agent's primary LAN IPv4 on the site network, reported on enrollment
    /// and each heartbeat. Site clients are pointed at this address for LAN speed
    /// tests (OpenSpeedTest and iperf3), since the central server's own address is
    /// unreachable from a remote site's LAN. Null until an agent reports it.
    /// </summary>
    [MaxLength(45)]
    public string? LanIp { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
