using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Persists each UniFi bridge's flow to the interface_counters InfluxDB measurement so the LAN
/// Flow Map's HISTORIC resolver can re-derive it: a Device Bridge (UDB) from its downlink
/// port_table, a Building Bridge (UBB) from the wirelessly-uplinked unit's uplink counters.
///
/// The live path reads a bridge's rate from <see cref="LanFabricAggregator"/> + MonitoringLiveStats,
/// which are in-memory only. Every other device's boundary aggregate has a durable
/// interface_counters series to re-derive from during playback (an SNMP uplink port, or a mesh
/// AP's vwiresta interface). A bridge's throughput lives only in UniFi-API signals that are never
/// SNMP-polled, so without this it has no durable series at all.
///
/// One "bridge-downlink" series is written per bridge, matching the live WriteAggregates
/// aggregate. Shared by the directly-monitored fast tier (MonitoringCollectionAgent) and the
/// agent-relayed path (AgentProbeResultSink) so both site types record identically.
/// </summary>
public static class BridgeInterfaceRecorder
{
    /// <summary>
    /// Synthetic interface name for a bridge's single-flow series in interface_counters: a UDB's
    /// summed downlink port_table, or a UBB unit's wireless span. The historic MeshBackhaul and
    /// WiredClient resolvers match on it, and the fabric-sum badge excludes it.
    /// </summary>
    public const string DownlinkIfName = "bridge-downlink";

    /// <summary>
    /// Writes the bridged flow of every DeviceBridge and wirelessly-uplinked BuildingBridge unit
    /// in <paramref name="devices"/> (nested UBB peers included) to interface_counters. Call right
    /// after <see cref="LanFabricAggregator.WriteAggregates"/> (its live sibling), once
    /// <see cref="LanFabricAggregator.UpdateUnifiPortRates"/> has run so the rates are populated.
    /// No-op until a rate is available (needs a prior byte sample to delta).
    /// </summary>
    public static void Record(
        LanFabricAggregator fabric,
        IReadOnlyList<UniFiDeviceResponse> devices,
        MonitoringInfluxClient influx,
        DateTime now)
    {
        if (!influx.IsConfigured) return;

        foreach (var dev in devices.Concat(UniFiDiscovery.BuildingBridgePeers(devices)))
        {
            if (LanFabricAggregator.IsWirelessBridgeUnit(dev))
            {
                RecordBuildingBridgeSpan(fabric, dev, influx, now);
                continue;
            }
            if (dev.DeviceType != DeviceType.DeviceBridge || dev.PortTable == null) continue;

            var devMac = dev.Mac.ToLowerInvariant().Replace("-", ":");
            double downBps = 0, upBps = 0;
            long txBytes = 0, rxBytes = 0;
            long? speedBps = null;
            bool anyRate = false;

            foreach (var port in dev.PortTable)
            {
                if (port.IsUplink) continue;
                var rate = fabric.PortRate(devMac, port.PortIdx);
                if (!rate.HasValue) continue;

                // PortRate tuple: DownBps = RX-derived = bytes FROM the bridged client = upload
                // (upstream, toward the gateway); UpBps = TX-derived = bytes TO the client =
                // download (downstream). Persist in interface_counters' convention, where
                // rateIn = downstream/download and rateOut = upstream/upload, so the historic
                // resolvers read it with the same (Down = rateIn, Up = rateOut) mapping they use
                // for a vwiresta or SNMP series.
                downBps += rate.Value.UpBps;
                upBps += rate.Value.DownBps;
                txBytes += port.TxBytes;
                rxBytes += port.RxBytes;
                if (port.Speed > 0) speedBps = Math.Max(speedBps ?? 0, (long)port.Speed * 1_000_000L);
                anyRate = true;
            }

            if (!anyRate) continue;

            _ = influx.WriteInterfaceCountersAsync(
                deviceMac: devMac,
                ifName: DownlinkIfName,
                portId: null,
                direction: InterfaceDirection.Unknown,
                bytesIn: txBytes,   // rateIn = download  -> port TX bytes (toward the client)
                bytesOut: rxBytes,  // rateOut = upload    -> port RX bytes (from the client)
                rateInBps: downBps,
                rateOutBps: upBps,
                speedBps: speedBps,
                operStatus: 1,
                errorsIn: 0,
                errorsOut: 0,
                discardsIn: 0,
                discardsOut: 0,
                hcCounters: true,
                ucastPktsIn: null,
                ucastPktsOut: null,
                mcastPktsIn: null,
                mcastPktsOut: null,
                bcastPktsIn: null,
                bcastPktsOut: null,
                timestamp: now);
        }
    }

    /// <summary>
    /// A UBB unit's wireless span, from its own uplink counters (unit perspective: rx = from the
    /// peer = downloads, tx = toward it = uploads), in interface_counters' rateIn = download
    /// convention. Speed is the span's PHY rate so the series reads like a port.
    /// </summary>
    private static void RecordBuildingBridgeSpan(
        LanFabricAggregator fabric, UniFiDeviceResponse dev, MonitoringInfluxClient influx, DateTime now)
    {
        var devMac = dev.Mac.ToLowerInvariant().Replace("-", ":");
        var rate = fabric.UplinkRate(devMac);
        if (!rate.HasValue || dev.Uplink == null) return;

        var phyKbps = Math.Max(dev.Uplink.TxRate, dev.Uplink.RxRate);
        _ = influx.WriteInterfaceCountersAsync(
            deviceMac: devMac,
            ifName: DownlinkIfName,
            portId: null,
            direction: InterfaceDirection.Unknown,
            bytesIn: dev.Uplink.RxBytes,
            bytesOut: dev.Uplink.TxBytes,
            rateInBps: rate.Value.DownBps,
            rateOutBps: rate.Value.UpBps,
            speedBps: phyKbps > 0 ? phyKbps * 1_000L : null,
            operStatus: 1,
            errorsIn: 0,
            errorsOut: 0,
            discardsIn: 0,
            discardsOut: 0,
            hcCounters: true,
            ucastPktsIn: null,
            ucastPktsOut: null,
            mcastPktsIn: null,
            mcastPktsOut: null,
            bcastPktsIn: null,
            bcastPktsOut: null,
            timestamp: now);
    }
}
