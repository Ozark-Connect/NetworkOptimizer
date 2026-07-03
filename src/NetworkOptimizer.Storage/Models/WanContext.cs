using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// A per-site multi-WAN monitoring context (spec section 3). The default
/// context ("primary") is implicit - no row exists for it, and targets with a
/// null <see cref="MonitoringTarget.WanContextId"/> belong to it, keeping
/// existing installs unchanged. Additional contexts describe a secondary WAN:
/// probes for targets in the context either bind to <see cref="ProbeSourceIp"/>
/// locally (the gateway policy-routes that source IP out the WAN) or run on the
/// assigned probe-only agent. Lives in each site's own database; the context
/// name becomes the `wan` tag on latency points, emitted only for non-default
/// contexts so the Influx schema stays additive-only.
/// </summary>
public class WanContext
{
    [Key]
    public int Id { get; set; }

    /// <summary>Display name, also the Influx `wan` tag value (e.g. "starlink-backup").</summary>
    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>
    /// Source IP local probes bind to for this context's targets (ping -I /
    /// TCP socket bind). The gateway policy-routes this IP out the WAN being
    /// measured. Null when the context is probed by an assigned agent instead.
    /// </summary>
    [MaxLength(50)]
    public string? ProbeSourceIp { get; set; }

    /// <summary>
    /// Agent (SiteAgents id from the main registry database - a loose reference,
    /// not a foreign key) that probes this context's targets, typically a
    /// probe-only agent bound to the WAN's source IP. When set, the server's
    /// local prober skips these targets and only this agent receives them.
    /// </summary>
    public int? AgentId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
