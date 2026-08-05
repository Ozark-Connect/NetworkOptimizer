using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// A per-site multi-WAN monitoring context (spec section 3). The default
/// context ("primary") is implicit - no row exists for it, and targets with a
/// null <see cref="MonitoringTarget.WanContextId"/> belong to it, keeping
/// existing installs unchanged. Additional contexts describe a secondary WAN:
/// probes for targets in the context either bind to <see cref="ProbeSourceIp"/>
/// locally (the gateway policy-routes that source IP out the WAN) or run on the
/// assigned probe-only agent. Lives in each site's own database;
/// <see cref="InfluxWanTag"/> becomes the `wan` tag on latency points, emitted
/// only for non-default contexts so the Influx schema stays additive-only.
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

    /// <summary>
    /// Exact interface the assigned agent binds its probes to (<c>eth8</c>,
    /// <c>ppp0</c>), for an agent running on the gateway itself: the probe
    /// leaves by that WAN's own data path rather than by whatever the routing
    /// table prefers. Only meaningful alongside <see cref="AgentId"/> - the
    /// server does not sit on the gateway, so an interface name it cannot see
    /// binds nothing. Null for source-IP contexts and for agents that probe on
    /// their own default route.
    /// </summary>
    [MaxLength(50)]
    public string? InterfaceName { get; set; }

    /// <summary>
    /// The UniFi WAN key this context measures (<c>wan</c>, <c>wan2</c>), picked
    /// from the site's real WANs rather than typed. It is what says where the
    /// context's data belongs: the Influx <c>wan</c> tag, the ISP Health report
    /// it associates with, and the scope of its upstream discovery. Required on
    /// every new context regardless of bind mechanism; nullable only because
    /// contexts created before this column existed have no value to backfill
    /// from.
    /// </summary>
    [MaxLength(50)]
    public string? WanInterface { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Value written to the Influx <c>wan</c> tag for this context's points: the
    /// stable UniFi WAN key when the context has one, falling back to the
    /// display name for contexts predating <see cref="WanInterface"/>. The key
    /// survives a rename, which the name does not - a renamed context used to
    /// orphan its own history under the old tag value.
    /// </summary>
    [NotMapped]
    public string InfluxWanTag => string.IsNullOrEmpty(WanInterface) ? Name : WanInterface!;
}
