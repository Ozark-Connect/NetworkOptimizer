using System.Collections.Concurrent;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Picks the gateway port-table snapshots worth recording as link speed history.
/// <para>
/// The console port table is the only source of a gateway's negotiated port speed: its built-in
/// switch ports are VLAN interfaces on the switch conduit, so SNMP reports the conduit's 10G for all
/// of them. The snapshot is never paired with an SNMP sample, so the two sources' timing can never
/// disagree in the history.
/// </para>
/// <para>
/// A snapshot counts only when the gateway is online and its <c>last_seen</c> (last inform) has
/// advanced since the last one taken. A cached device list, a console still answering with an old
/// list, and a gateway that stopped informing all repeat the same <c>last_seen</c>, and none of them
/// is a new observation. <c>last_seen</c> is compared only with itself, never with this server's
/// clock, so console clock skew cannot reject or reorder snapshots.
/// </para>
/// </summary>
public sealed class GatewayLinkSpeedSnapshots
{
    private readonly ConcurrentDictionary<string, long> _lastTakenSeen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One gateway port as of one fresh snapshot.</summary>
    /// <param name="DeviceMac">Gateway MAC, normalized lower-case.</param>
    /// <param name="DeviceName">Gateway name.</param>
    /// <param name="IfName">Kernel interface name (<c>ifname</c>), which the SNMP series is named by.</param>
    /// <param name="PortName">The port's name in UniFi.</param>
    /// <param name="NetworkName">UniFi network the port serves, e.g. "wan", "wan2", "lan".</param>
    /// <param name="SpeedMbps">Negotiated speed; null when the port is down or reports none.</param>
    /// <param name="FullDuplex">Negotiated duplex, when reported and the port is up.</param>
    /// <param name="Autoneg">Whether the port auto-negotiates (false = speed forced in UniFi).</param>
    /// <param name="Media">The port's UniFi <c>media</c>, e.g. "GE", "2.5GE", "10GE", "SFP+".</param>
    public sealed record PortReading(
        string DeviceMac,
        string DeviceName,
        string IfName,
        string PortName,
        string? NetworkName,
        int? SpeedMbps,
        bool? FullDuplex,
        bool Autoneg,
        string? Media);

    /// <summary>
    /// The ports of every gateway with a new snapshot. Gateways with nothing new contribute nothing.
    /// </summary>
    /// <param name="devices">The site's device list, as the console returned it.</param>
    public IReadOnlyList<PortReading> Take(IEnumerable<UniFiDeviceResponse> devices)
    {
        var readings = new List<PortReading>();
        foreach (var device in devices)
        {
            if (device.DeviceType != DeviceType.Gateway || !device.Adopted
                || string.IsNullOrEmpty(device.Mac) || device.PortTable is not { Count: > 0 }
                || device.LastSeen <= 0 || !UniFiDeviceStateMap.IsOnline(device.State))
                continue;

            var mac = device.Mac.Trim().ToLowerInvariant();
            if (_lastTakenSeen.TryGetValue(mac, out var taken) && device.LastSeen <= taken) continue;
            _lastTakenSeen[mac] = device.LastSeen;

            var name = string.IsNullOrWhiteSpace(device.Name) ? mac : device.Name;
            foreach (var port in device.PortTable)
            {
                if (port.PortIdx <= 0 || string.IsNullOrWhiteSpace(port.IfName)) continue;
                // A down port's speed is meaningless (the console reports 10 for some), so it
                // carries no speed rather than a value that reads as a 10 Mbps link.
                var up = port.Up && port.Speed > 0;
                readings.Add(new PortReading(
                    mac,
                    name,
                    port.IfName.Trim(),
                    string.IsNullOrWhiteSpace(port.Name) ? port.IfName.Trim() : port.Name.Trim(),
                    port.NetworkName,
                    up ? port.Speed : null,
                    up ? port.FullDuplex : null,
                    port.Autoneg,
                    port.Media));
            }
        }
        return readings;
    }
}
