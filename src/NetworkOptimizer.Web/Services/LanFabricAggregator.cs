using System.Collections.Concurrent;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Owns the per-port and per-device byte-rate caches and the topology-boundary
/// aggregate computation (spec 5.6: a device's throughput is the rate on the
/// uplink port that bounds it, not the sum of all its interfaces - which would
/// double-count fabric-crossing and purely-local traffic).
///
/// Extracted from MonitoringCollectionAgent so the agent-relayed path
/// (AgentProbeResultSink, secondary sites) runs the byte-for-byte identical
/// aggregate logic instead of a hand-copied duplicate that could drift on the
/// subtle direction conventions. One instance per site: MonitoringCollectionAgent
/// holds one (it is itself per-site); AgentProbeResultSink keeps one per slug.
///
/// The caller feeds rates in from its own per-interface loop
/// (<see cref="SetSnmpPortRate"/>, the primary 5s-cadence source) and from the
/// UniFi port_table (<see cref="UpdateUnifiPortRates"/>, the fallback), then calls
/// <see cref="WriteAggregates"/> to publish each device's boundary rate to live
/// stats. Fabric sums (sum of physical interface rates) stay in the caller's loop
/// because they need each interface's ifDescr + device type.
/// </summary>
public sealed class LanFabricAggregator
{
    private readonly record struct PortByteSnapshot(DateTime Timestamp, long TxBytes, long RxBytes);

    // Per-port rate state for AP backhaul lookups. _portBytePrev stores the last byte
    // counter sample so we can diff; _portRateLatest holds the most recent computed
    // rate keyed identically.
    private readonly ConcurrentDictionary<(string SwitchMac, int PortIdx), PortByteSnapshot> _portBytePrev = new();
    private readonly ConcurrentDictionary<(string SwitchMac, int PortIdx), (double DownBps, double UpBps)> _portRateLatest = new();
    // Device-level byte counters from UniFi's `stat.tx_bytes`/`rx_bytes`. Keyed by
    // device MAC (normalized). Mirrors the port cache shape; used as the mesh-AP
    // fallback when no parent switch port rate is available.
    private readonly ConcurrentDictionary<string, PortByteSnapshot> _deviceBytePrev = new();
    private readonly ConcurrentDictionary<string, (double DownBps, double UpBps)> _deviceByteRateLatest = new();

    /// <summary>Latest computed rate for a switch port, or null.</summary>
    public (double DownBps, double UpBps)? PortRate(string switchMac, int portIdx) =>
        _portRateLatest.TryGetValue((switchMac, portIdx), out var v) ? v : null;

    /// <summary>UniFi device-level byte-counter delta. Fallback for mesh-uplinked APs.</summary>
    public (double DownBps, double UpBps)? DeviceRate(string deviceMac) =>
        _deviceByteRateLatest.TryGetValue(deviceMac, out var v) ? v : null;

    /// <summary>
    /// Sets the SNMP-derived rate for a port (the primary, 5s-cadence source). The
    /// caller passes (rateIn=RX, rateOut=TX) so the cache stays consistent with the
    /// UniFi writer below. Overrides any UniFi port_table delta for the same key.
    /// </summary>
    public void SetSnmpPortRate(string mac, int portIdx, double rateInBps, double rateOutBps) =>
        _portRateLatest[(mac, portIdx)] = (rateInBps, rateOutBps);

    /// <summary>
    /// Diff the latest port_table byte counters against the previous reading to compute
    /// per-port bps rates. Populates _portRateLatest, which AP-rate post-processing
    /// reads from. Spec 5.6: AP rates come from the switch port they're plugged into,
    /// not from the AP's own SNMP counters.
    /// </summary>
    public void UpdateUnifiPortRates(IReadOnlyList<UniFiDeviceResponse> devices, DateTime now)
    {
        foreach (var device in devices)
        {
            if (device.PortTable == null || device.PortTable.Count == 0) continue;
            var mac = NormalizeMac(device.Mac);
            foreach (var port in device.PortTable)
            {
                if (port.PortIdx <= 0) continue;
                var key = (mac, port.PortIdx);
                var current = new PortByteSnapshot(now, port.TxBytes, port.RxBytes);
                if (_portBytePrev.TryGetValue(key, out var prev))
                {
                    var elapsed = (now - prev.Timestamp).TotalSeconds;
                    if (elapsed > 0.5)
                    {
                        long deltaTx = current.TxBytes - prev.TxBytes;
                        long deltaRx = current.RxBytes - prev.RxBytes;
                        if (deltaTx >= 0 && deltaRx >= 0)
                        {
                            if (deltaTx == 0 && deltaRx == 0)
                            {
                                // Counters unchanged - UniFi hasn't refreshed server-side
                                // yet. Keep the previous snapshot so the next real change
                                // computes over the true elapsed window.
                            }
                            else
                            {
                                // Tuple convention is aligned with the SNMP writer
                                // (SetSnmpPortRate) so downstream consumers see stable
                                // directions whether SNMP or this UniFi PortTable writer
                                // ran last on a given cycle: tuple = (rateIn=RX, rateOut=TX).
                                // NOTE: do NOT mirror into _liveStats per-port cache here.
                                // UniFi PortTable byte counters update server-side ~30s; at
                                // our 5s poll cadence that yields a burst-then-zeros pattern
                                // that would clobber the SNMP-fed RecordPortRate writes.
                                _portRateLatest[key] = (deltaRx * 8.0 / elapsed, deltaTx * 8.0 / elapsed);
                                _portBytePrev[key] = current;
                            }
                        }
                    }
                }
                if (!_portBytePrev.ContainsKey(key))
                    _portBytePrev[key] = current;
            }

            // Device-level aggregate: UniFi's stat.tx_bytes / rx_bytes is the
            // AP-aggregated counter (UniFi normalizes radio + Ethernet into one
            // honest number). Useful as a fallback for mesh-uplinked APs where
            // there's no parent switch port to read.
            if (device.Stats != null)
            {
                var devKey = mac;
                var devCurrent = new PortByteSnapshot(now, device.Stats.TxBytes, device.Stats.RxBytes);
                if (_deviceBytePrev.TryGetValue(devKey, out var devPrev))
                {
                    var elapsed = (now - devPrev.Timestamp).TotalSeconds;
                    if (elapsed > 0.5)
                    {
                        long deltaTx = devCurrent.TxBytes - devPrev.TxBytes;
                        long deltaRx = devCurrent.RxBytes - devPrev.RxBytes;
                        if (deltaTx >= 0 && deltaRx >= 0)
                        {
                            if (deltaTx == 0 && deltaRx == 0)
                            {
                                // Counters unchanged - keep previous snapshot.
                            }
                            else
                            {
                                // Device perspective: TX = device sends out (upstream away
                                // from the device); RX = device receives (downstream toward
                                // the device). Opposite convention vs the port path above.
                                _deviceByteRateLatest[devKey] = (deltaRx * 8.0 / elapsed, deltaTx * 8.0 / elapsed);
                                _deviceBytePrev[devKey] = devCurrent;
                            }
                        }
                    }
                }
                if (!_deviceBytePrev.ContainsKey(devKey))
                    _deviceBytePrev[devKey] = devCurrent;
            }
        }
    }

    /// <summary>
    /// Writes each device's topology-boundary aggregate to live stats: AP/switch
    /// uplink-port rates, mesh-AP synthesis, and gateway WAN rates, with the exact
    /// fallbacks the directly-monitored fast tier uses. Call after the per-interface
    /// loop has fed in SNMP port rates and UpdateUnifiPortRates has run. Fabric sums
    /// (sum of physical interface rates) are written by the caller from its loop.
    /// </summary>
    public void WriteAggregates(IReadOnlyList<UniFiDeviceResponse> devices, MonitoringLiveStats liveStats, DateTime now)
    {
        // A parent's downlink_table claim contradicting a device's own uplink field means that
        // uplink block is stale and describes the wrong link - reading the port it names reports
        // the mesh backhaul backwards (and repeatedly overwrites the correct vwiresta aggregate).
        // Such devices skip the port-based first pass and take the wireless-mesh path instead.
        // A claim that AGREES with the device's own uplink changes nothing.
        var meshClaims = UniFiDiscovery.BuildMeshParentByChild(devices);
        bool HasContradictedUplink(UniFiDeviceResponse d) =>
            meshClaims.TryGetValue(NormalizeMac(d.Mac), out var claim)
            && claim.Contradicts(d.Uplink?.UplinkMac);

        // Post-process: override device aggregates with their parent-uplink-port
        // counters. For APs (spec 5.6) we already did this. For switches and gateways,
        // summing every interface counter on the device double-counts traffic that
        // crosses the switch fabric port-to-port and includes purely local traffic
        // that never crosses the uplink - both of which inflate the "device activity"
        // the topology cares about. The trunk/uplink port is the boundary between
        // the device and the rest of the network, which is what the topology pipe
        // actually carries.
        foreach (var dev in devices.Where(d => d.Uplink != null
                                               && !string.IsNullOrEmpty(d.Uplink.UplinkMac)
                                               && (d.DeviceType == DeviceType.AccessPoint
                                                   || d.DeviceType == DeviceType.Switch)
                                               && !HasContradictedUplink(d)))
        {
            var devMac = NormalizeMac(dev.Mac);
            var parentMac = NormalizeMac(dev.Uplink!.UplinkMac);
            var portIdx = dev.Uplink.UplinkRemotePort;
            (double DownBps, double UpBps)? rate = null;

            // Primary path: parent switch port byte delta. Works for wired-uplinked APs
            // and switches. Direction matches live-stats convention since the port
            // counter is read from the SWITCH's perspective (TX = toward child).
            if (portIdx > 0)
                rate = PortRate(parentMac, portIdx);

            if (rate.HasValue)
            {
                liveStats.RecordInterfaceAggregate(dev.Mac, rate.Value.DownBps, rate.Value.UpBps, now);
            }
            else if (dev.DeviceType == DeviceType.Switch
                     && dev.Uplink?.PortIdx is int localUpIdx)
            {
                // Parent didn't expose a usable port_table rate (common when
                // the parent is a mesh AP, whose Ethernet downlink isn't in
                // its port_table). Read this switch's OWN port_table entry
                // for its uplink port instead - UniFi populates tx/rx_bytes
                // on the switch's side of that link too.
                var ownRate = PortRate(NormalizeMac(dev.Mac), localUpIdx);
                if (ownRate.HasValue)
                {
                    // Direction note: parent.port.tx_bytes captures "bytes the
                    // connected device transmitted" so the parent-path stores
                    // child.RateInBps = uploads-from-child. The switch's OWN
                    // uplink port observes the same physical wire from the
                    // other side, so its tx_bytes = bytes the PARENT
                    // transmitted = downloads to this switch. Swap the
                    // (DownBps, UpBps) args so RateInBps remains "uploads"
                    // and stays consistent with the primary path.
                    liveStats.RecordInterfaceAggregate(dev.Mac, ownRate.Value.UpBps, ownRate.Value.DownBps, now);
                }
            }
            else if (dev.DeviceType == DeviceType.AccessPoint)
            {
                // Wired APs without a usable parent-port rate fall back to UniFi's
                // device-level tx_bytes / rx_bytes delta. Mesh APs get their
                // aggregate from the SNMP vwiresta interface in the fast-tier
                // task above; if SNMP failed, this fallback fires for them too.
                // DeviceRate returns (down, up) in our convention.
                var devRate = DeviceRate(devMac);
                if (devRate.HasValue)
                    liveStats.RecordInterfaceAggregate(dev.Mac, devRate.Value.DownBps, devRate.Value.UpBps, now);
            }

            // Switch fabric sum (sum(rx) / sum(tx)) is written by the SNMP
            // fast tier directly into _liveStats.FabricIngressBps/EgressBps,
            // since the SNMP per-interface rates are on a clean 5s cadence
            // (UniFi's PortTable byte counters refresh server-side ~30s and
            // would produce a one-burst / many-zeroes pattern here).
        }

        // Second pass: wirelessly-uplinked devices - mesh APs AND single-port bridges
        // (UDBs) - need a custom aggregate because UniFi's device-level stat.tx_bytes /
        // rx_bytes doesn't reliably include traffic shuttled across the wireless backhaul,
        // and there's no parent switch port to read (the first pass found nothing for them).
        // Wired APs/switches don't need this: their parent port already sees every byte.
        // We synthesize the aggregate from whichever of these contributors apply:
        //   (a) downstream UniFi devices (switch or another AP plugged into an Ethernet
        //       downlink) - their boundary aggregates were just written in the first pass.
        //   (b) wireless clients directly associated to a mesh AP - their TX/RX throughput
        //       maps onto the backhaul flow.
        //   (c) for a UDB, the bridged client is a plain wired client (neither a UniFi child
        //       nor a wifi client), so its throughput lives on the bridge's OWN downlink
        //       port_table; read that. Restricted to DeviceBridge so a mesh AP never
        //       double-counts its downstream, which is already covered by (a).
        // NetworkPathAnalyzer treats device.Uplink.Type == "wireless" as the mesh marker;
        // mirror that here for consistency with how the speed-test path tracer identifies
        // mesh hops.
        // A contradicted uplink also qualifies: the child IS wirelessly uplinked (the parent's
        // downlink_table proves it), its stale uplink block just still says "wire".
        foreach (var dev in devices.Where(d =>
            (d.DeviceType == DeviceType.AccessPoint || d.DeviceType == DeviceType.DeviceBridge)
            && d.Uplink != null
            && (string.Equals(d.Uplink.Type, "wireless", StringComparison.OrdinalIgnoreCase)
                || HasContradictedUplink(d))))
        {
            var devMac = NormalizeMac(dev.Mac);
            double sumIn = 0, sumOut = 0;
            bool anyContribution = false;

            // (a) downstream UniFi children on the Ethernet downlink.
            foreach (var child in devices)
            {
                if (child.Uplink == null || string.IsNullOrEmpty(child.Uplink.UplinkMac)) continue;
                if (!string.Equals(NormalizeMac(child.Uplink.UplinkMac), devMac, StringComparison.OrdinalIgnoreCase)) continue;
                var stats = liveStats.GetForDevice(child.Mac);
                if (stats == null || !stats.LastRateUpdate.HasValue) continue;
                sumIn += stats.RateInBps ?? 0;
                sumOut += stats.RateOutBps ?? 0;
                anyContribution = true;
            }

            // (b) wireless clients on the mesh AP itself. TxThroughputBps
            // is AP -> client (downloads, gateway-relative), Rx is the
            // reverse (uploads). Sum onto the same RateIn / RateOut sides
            // the children write so the totals stay direction-consistent.
            // A UDB is not an AP, so this yields nothing for it.
            foreach (var wc in liveStats.GetWifiClientsForAp(dev.Mac))
            {
                var rx = wc.RxThroughputBps ?? 0;
                var tx = wc.TxThroughputBps ?? 0;
                if (rx > 0 || tx > 0)
                {
                    sumIn += rx;
                    sumOut += tx;
                    anyContribution = true;
                }
            }

            // (c) UDB bridged wired client: its bytes live on the bridge's own downlink
            // port_table. Same direction convention as a parent reading a port toward a
            // child (port RX = uploads from the client, TX = downloads to it), so add
            // (DownBps, UpBps) onto the same (sumIn, sumOut) sides as (a)/(b). DeviceBridge
            // only - a mesh AP's own downstream is already counted in (a).
            if (dev.DeviceType == DeviceType.DeviceBridge && dev.PortTable != null)
            {
                foreach (var port in dev.PortTable)
                {
                    if (port.IsUplink) continue;
                    var portRate = PortRate(devMac, port.PortIdx);
                    if (portRate.HasValue)
                    {
                        sumIn += portRate.Value.DownBps;
                        sumOut += portRate.Value.UpBps;
                        anyContribution = true;
                    }
                }
            }

            // SNMP first-pass (vwiresta) is the most accurate source. Only
            // overwrite if SNMP didn't get a chance (e.g. AP unreachable via
            // SNMP) - i.e. the live-stats entry has no aggregate yet.
            if (anyContribution)
            {
                var existing = liveStats.GetForDevice(dev.Mac);
                bool snmpAlreadySet = existing?.RateInBps.HasValue == true
                                      || existing?.RateOutBps.HasValue == true;
                if (!snmpAlreadySet)
                {
                    liveStats.RecordInterfaceAggregate(dev.Mac, sumIn, sumOut, now);
                }
            }
        }

        // Gateways are the top of the topology - no parent switch to read their
        // uplink rate from. Use the SNMP rate on the gateway's actual WAN
        // interface (wan1...wan6 uplink_ifname, e.g. eth6.100 for VLAN-tagged
        // or ppp3 for PPPoE) rather than port_table.IsUplink which may not be
        // set for non-standard connection types.
        foreach (var gw in devices.Where(d => d.DeviceType == DeviceType.Gateway))
        {
            var gwMac = NormalizeMac(gw.Mac);

            // SNMP rate on the WAN interface: ppp* tunnel for PPPoE, physical
            // port otherwise (issue #669). The physical port stays active under
            // PPPoE and over-counts (overhead + sibling VLANs), while VLAN
            // sub-interfaces double-count on some kernels. The other name remains
            // a fallback in case the preferred interface has no SNMP rate yet.
            var (physIfName, uplinkIfName) = UniFiDiscovery.ResolveActiveWanInterface(gw);
            var preferred = NetworkUtilities.PreferredWanCounterInterface(physIfName, uplinkIfName);
            var fallback = preferred == physIfName ? uplinkIfName : physIfName;
            var portRate = preferred != null ? liveStats.GetPortRate(gwMac, preferred) : null;
            if (portRate == null && fallback != null && fallback != preferred)
                portRate = liveStats.GetPortRate(gwMac, fallback);
            if (portRate != null)
            {
                // Gateway WAN perspective: port TX (DownBps) = data toward
                // internet = uploads; port RX (UpBps) = from internet = downloads.
                liveStats.RecordInterfaceAggregate(gw.Mac, portRate.DownBps, portRate.UpBps, now);
                continue;
            }

            // Fallback: PortTable IsUplink + PortIdx. Covers gateways where
            // the raw JSON hasn't been fetched yet or the wan object is missing.
            (double DownBps, double UpBps)? rate = null;
            if (gw.PortTable != null)
            {
                var wanPort = gw.PortTable.FirstOrDefault(p => p.IsUplink);
                if (wanPort != null && wanPort.PortIdx > 0)
                {
                    rate = PortRate(gwMac, wanPort.PortIdx);
                    if (rate.HasValue)
                    {
                        liveStats.RecordInterfaceAggregate(gw.Mac, rate.Value.UpBps, rate.Value.DownBps, now);
                        continue;
                    }
                }
            }

            // Last resort: device-level tx_bytes / rx_bytes delta. Used when the
            // gateway's WAN-side is a bond/LAG and neither wan1...wan6 nor
            // PortTable produced a usable rate.
            var devRate = DeviceRate(gwMac);
            if (devRate.HasValue)
            {
                liveStats.RecordInterfaceAggregate(gw.Mac, devRate.Value.DownBps, devRate.Value.UpBps, now);
            }
        }

        liveStats.Prune(TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Whether an SNMP interface should contribute to the device's fabric ingress/
    /// egress sum. Switches expose only physical "Port N" entries so they're safe
    /// to sum wholesale. Gateways expose a zoo of pseudo-interfaces (VLAN sub-
    /// interfaces like eth5.200, bridges br0/br200/..., bond0, the internal
    /// switch-chip alias switch0[.X], honeypot*, wgclt*, gre*, *_vti, etc.) that
    /// all alias counters carried by a physical eth port. Summing those gives
    /// 3-4x the real total, so we restrict gateway fabric sum to plain ethN.
    /// </summary>
    public static bool IncludeInFabricSum(DeviceType type, string ifDescr)
    {
        if (type == DeviceType.Gateway)
            return System.Text.RegularExpressions.Regex.IsMatch(ifDescr, @"^eth\d+$");
        return true;
    }

    private static string NormalizeMac(string mac) =>
        string.IsNullOrEmpty(mac) ? string.Empty : mac.ToLowerInvariant().Replace('-', ':');
}
