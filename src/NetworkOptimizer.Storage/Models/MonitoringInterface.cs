using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// A persisted "monitoring interface": a macvlan + route + optional SNAT that the
/// gateway installs so the Network Optimizer server (a LAN client) and browsers can
/// reach an ONT/modem management IP that sits behind the WAN. Deployed as a
/// self-contained, idempotent boot script plus a cron watchdog so it survives reboots
/// and UniFi reprovisioning (same model as Performance Tweaks and Adaptive SQM).
/// </summary>
public class MonitoringInterface
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Name of the macvlan interface created on the gateway (e.g., "modem0").
    /// Editable for power users; kept short and interface-name safe.
    /// </summary>
    [Required]
    [MaxLength(15)]
    public string Name { get; set; } = "modem0";

    /// <summary>
    /// Physical WAN port the macvlan rides on (e.g., "eth6"). This is the
    /// GatewayWanInterface.IfName (the parent port), NOT the logical uplink - for
    /// PPPoE/VLAN WANs the macvlan must attach to the physical port, not ppp0/eth4.100.
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string WanIfName { get; set; } = "";

    /// <summary>
    /// Optional 802.1Q VLAN ID for the WAN. When set, the macvlan attaches to the VLAN
    /// subinterface (e.g. "eth6.100") instead of the bare physical port, so frames are
    /// tagged. The gateway creates that subinterface if it doesn't already exist (UniFi
    /// creates it itself when the WAN runs on the VLAN). Null = untagged (raw port).
    /// </summary>
    public int? WanVlanId { get; set; }

    /// <summary>
    /// WAN object key this interface targets (e.g., "wan1", "wan2"), used to
    /// re-derive the WAN display label and disambiguate dual-WAN setups. Optional.
    /// </summary>
    [MaxLength(10)]
    public string? WanKey { get; set; }

    /// <summary>Management IP of the ONT/modem to reach (e.g., "192.168.100.1").</summary>
    [Required]
    [MaxLength(64)]
    public string TargetIp { get; set; } = "";

    /// <summary>CIDR prefix length of the modem's management subnet (default /24).</summary>
    public int SubnetPrefix { get; set; } = 24;

    /// <summary>
    /// The gateway's LAN-local IP on the modem subnet - the address assigned to the
    /// macvlan that the gateway and SNAT'd LAN clients source from when reaching the
    /// modem (e.g., "192.168.100.2"). Auto-suggested as target host +/-1, editable.
    /// </summary>
    [Required]
    [MaxLength(64)]
    public string GatewayLocalIp { get; set; } = "";

    /// <summary>
    /// Alternate IP the LAN polls instead of TargetIp, for the duplicate-IP case where two
    /// WANs' devices share the same management IP (e.g. a modem and a Starlink dish both at
    /// 192.168.100.1). When set, the gateway DNATs AliasIp -> TargetIp pinned to this
    /// interface's WAN via fwmark policy routing, and installs no main-table route for
    /// TargetIp. Null (the default) means normal direct routing - unrelated to this field.
    /// </summary>
    [MaxLength(64)]
    public string? AliasIp { get; set; }

    /// <summary>
    /// Whether to add a narrow SNAT rule so LAN clients (including the Network
    /// Optimizer server's pollers) masquerade to the gateway-local IP when reaching
    /// the modem. Required for LAN-side access; on by default.
    /// </summary>
    public bool SnatEnabled { get; set; } = true;

    /// <summary>How often (minutes) the cron watchdog re-applies the config. Default 5.</summary>
    public int WatchdogIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// True when the user set this up by hand on the gateway and we only track/monitor
    /// it (no deploy/remove), mirroring the Performance Tweaks "manually deployed" mode.
    /// </summary>
    public bool IsManuallyDeployed { get; set; }

    /// <summary>Last deployment/repair error message (null if last action succeeded).</summary>
    [MaxLength(1000)]
    public string? LastError { get; set; }

    /// <summary>When this configuration was created.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When this configuration was last updated.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
