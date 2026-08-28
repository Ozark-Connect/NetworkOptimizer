using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// What this server knows about the AP Agent on one access point.
///
/// The AP Agent is ephemeral: it lives in tmpfs and dies with the AP's power-on session, so this
/// row is not an install record. It exists because two things must outlive the agent itself - the
/// bearer token, which a health poll after a server restart needs in order to talk to an agent that
/// is still running, and the operator's per-AP opt-out.
/// </summary>
public class ApAgentDeployment
{
    [Key]
    public int Id { get; set; }

    /// <summary>The access point's MAC, normalized to lower-case colon form. One row per AP.</summary>
    [Required]
    [MaxLength(20)]
    public string DeviceMac { get; set; } = "";

    /// <summary>Last known device name, for a readable audit trail when the console is unreachable.</summary>
    [MaxLength(128)]
    public string? DeviceName { get; set; }

    /// <summary>Whether this AP participates. False is an explicit per-AP opt-out.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Bearer token for this AP's agent, encrypted at rest.</summary>
    [MaxLength(500)]
    public string? Token { get; set; }

    /// <summary>Machine architecture last read over SSH (<c>uname -m</c>), e.g. "armv7l".</summary>
    [MaxLength(32)]
    public string? Architecture { get; set; }

    /// <summary>Agent release version last seen running on this AP.</summary>
    [MaxLength(64)]
    public string? DeployedVersion { get; set; }

    /// <summary>Agent contract version last seen running on this AP.</summary>
    public int? DeployedBinaryVersion { get; set; }

    /// <summary>When the binary was last pushed to this AP.</summary>
    public DateTime? LastDeployedAt { get; set; }

    /// <summary>When this AP's agent was last successfully reached over HTTP.</summary>
    public DateTime? LastHealthyAt { get; set; }

    /// <summary>Last failure reported for this AP, shown in the fleet table.</summary>
    [MaxLength(500)]
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
