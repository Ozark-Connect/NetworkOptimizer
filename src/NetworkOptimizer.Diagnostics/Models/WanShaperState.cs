namespace NetworkOptimizer.Diagnostics.Models;

/// <summary>
/// What the gateway's traffic control actually looks like on one WAN that has UniFi Smart Queues
/// turned on. Read over SSH and handed to the analyzer as plain data, so the check itself stays
/// free of any SSH or controller dependency.
///
/// Both directions are described because UniFi shapes them on different devices: egress rides the
/// WAN's own data-path interface, ingress rides the mirred "ifb" companion. A WAN can end up with
/// one and not the other.
/// </summary>
public class WanShaperState
{
    /// <summary>The WAN's display name in UniFi Network, used in the finding.</summary>
    public string WanName { get; init; } = string.Empty;

    /// <summary>
    /// The data-path interface: "eth6" plain, "eth6.100" VLAN-tagged, "ppp0" for PPPoE. This is
    /// the egress (upload) shaper's device.
    /// </summary>
    public string Interface { get; init; } = string.Empty;

    /// <summary>
    /// The ingress (download) shaper's device - "ifb" plus <see cref="Interface"/>. UniFi creates
    /// it when it provisions Smart Queues, so its absence is itself the symptom.
    /// </summary>
    public string IfbInterface { get; init; } = string.Empty;

    /// <summary>Configured Smart Queue download rate in Mbps, null or 0 when UniFi has none.</summary>
    public int? DownRateMbps { get; init; }

    /// <summary>Configured Smart Queue upload rate in Mbps, null or 0 when UniFi has none.</summary>
    public int? UpRateMbps { get; init; }

    /// <summary>What tc reported for <see cref="Interface"/>.</summary>
    public TcDeviceState Egress { get; init; } = new();

    /// <summary>What tc reported for <see cref="IfbInterface"/>.</summary>
    public TcDeviceState Ingress { get; init; } = new();
}

/// <summary>
/// One interface's traffic control state, as read from "tc class show dev &lt;name&gt;".
/// </summary>
public class TcDeviceState
{
    /// <summary>
    /// False when tc could not find the device at all. On the ifb companion that means UniFi never
    /// created it; on the WAN's own interface it means we resolved a name this box does not have,
    /// which is our problem rather than a finding.
    /// </summary>
    public bool DeviceFound { get; init; }

    /// <summary>
    /// True when tc reported an htb root class - the shaper actually running. A device with only
    /// the kernel's default "mq" classes is not being shaped.
    /// </summary>
    public bool HasRootHtb { get; init; }
}
