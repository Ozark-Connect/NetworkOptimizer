using System.Collections.Concurrent;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.Monitoring;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Records a switch's port_table counters to interface_counters while SNMP is not reading it:
/// SNMP off on the device, or excluded after failing (a USW-Flex-Mini never answers). The series
/// has the shape an SNMP walk writes for a switch port - the port's name as if_name, the port's
/// counters as the octet fields - so Port Statistics, the maps, and Client Performance's usage read
/// it the same way.
///
/// Never for a switch the SNMP path reads this cycle, so the two sources never write the same port
/// at once. Each keeps its own series (port_id differs), and the usage queries drop a delta that
/// spans longer than <see cref="HandoverGap"/>: that is one source resuming across the other's
/// stretch, which would otherwise be counted twice. Shared by the directly-monitored fast tier and
/// the agent-relayed path, as the bridge recorder is.
/// </summary>
public static class PortTableCounterRecorder
{
    /// <summary>
    /// How long SNMP may go unheard before its switch is taken over. Under SNMP's five-minute
    /// exclusion window, and the same bound the usage queries put on a counter delta.
    /// </summary>
    public static readonly TimeSpan HandoverGap = TimeSpan.FromMinutes(4);

    /// <summary>A switch port's if_name: its name, which is what an SNMP walk names it too.</summary>
    public static string IfNameFor(SwitchPort port) =>
        string.IsNullOrWhiteSpace(port.Name) ? $"Port {port.PortIdx}" : port.Name.Trim();

    /// <summary>
    /// The monitorable switches SNMP is not reading. <paramref name="whySkipped"/> says why the
    /// SNMP path is not reading a device, or null when it is.
    /// </summary>
    public static IEnumerable<UniFiDeviceResponse> Uncovered(
        IEnumerable<UniFiDeviceResponse> devices, Func<UniFiDeviceResponse, string?> whySkipped) =>
        devices.Where(d => d.DeviceType == DeviceType.Switch && d.PortTable is { Count: > 0 }
            && SnmpDeviceRules.IsMonitorable(d) && whySkipped(d) != null);

    // Switches currently on the port table, per path, so the hand-over is logged once each way.
    private static readonly ConcurrentDictionary<string, byte> _fallingBack = new();

    /// <summary>Writes every uncovered switch's ports for this pass. Returns the number of ports written.</summary>
    public static int Record(
        IEnumerable<UniFiDeviceResponse> devices,
        Func<UniFiDeviceResponse, string?> whySkipped,
        ConcurrentDictionary<string, InterfaceRateCalculator.State> counterCache,
        string cacheKeyPrefix,
        MonitoringInfluxClient influx,
        MonitoringLiveStats liveStats,
        ILogger logger,
        DateTime now)
    {
        var written = 0;
        foreach (var device in devices)
        {
            if (device.DeviceType != DeviceType.Switch || device.PortTable is not { Count: > 0 }
                || !SnmpDeviceRules.IsMonitorable(device)) continue;
            var mac = NormalizeMac(device.Mac);
            var stateKey = cacheKeyPrefix + mac;
            var reason = whySkipped(device);
            if (reason == null)
            {
                if (_fallingBack.TryRemove(stateKey, out _))
                    logger.LogDebug("Port stats for switch {Name} ({Mac}) are back on SNMP", device.Name, mac);
                continue;
            }
            if (_fallingBack.TryAdd(stateKey, 0))
                logger.LogDebug("Port stats for switch {Name} ({Mac}) are read from the UniFi API port table ({Ports} ports): {Reason}",
                    device.Name, mac, device.PortTable.Count, reason);
            foreach (var port in device.PortTable!)
            {
                if (port.PortIdx <= 0) continue;
                var ifName = IfNameFor(port);
                // Own keys, so the SNMP calculator state for the device is never touched.
                var key = $"{cacheKeyPrefix}port_table/{mac}/{ifName}";
                var speedBps = port.Speed > 0 ? (long)port.Speed * 1_000_000L : 0;
                var calc = InterfaceRateCalculator.Compute(
                    counterCache.TryGetValue(key, out var prev) ? prev : null,
                    port.RxBytes, port.TxBytes, now, useHcCounters: true, speedBps);
                counterCache[key] = calc.NewState;

                // The console refreshes these counters about every 30 s, so most passes hold the
                // baseline and carry no rate; the live rate stands until the next change.
                if (calc.RateInBps is { } rateIn && calc.RateOutBps is { } rateOut)
                    liveStats.RecordPortRate(mac, ifName, rateOut, rateIn, now);

                var portId = port.IfName ?? port.PortIdx.ToString();
                var operStatus = port.Up ? 1 : 2;
                liveStats.RecordPortStats(new MonitoringInfluxClient.PortStatsPoint
                {
                    DeviceMac = mac,
                    IfName = ifName,
                    PortId = portId,
                    OperStatus = operStatus,
                    SpeedBps = speedBps > 0 ? speedBps : null,
                    RateInBps = calc.RateInBps,
                    RateOutBps = calc.RateOutBps,
                    BytesIn = port.RxBytes,
                    BytesOut = port.TxBytes,
                    Time = now,
                });

                if (influx.IsConfigured)
                {
                    _ = influx.WriteInterfaceCountersAsync(
                        deviceMac: mac,
                        ifName: ifName,
                        portId: portId,
                        direction: InterfaceDirection.Unknown,
                        bytesIn: port.RxBytes,
                        bytesOut: port.TxBytes,
                        rateInBps: calc.RateInBps,
                        rateOutBps: calc.RateOutBps,
                        speedBps: speedBps > 0 ? speedBps : null,
                        operStatus: operStatus,
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
                written++;
            }
        }
        return written;
    }

    /// <summary>
    /// Name-map rows for an uncovered switch's ports, keyed as the series are, so a port's series
    /// resolves from its port number the way an SNMP switch's does.
    /// </summary>
    public static void ReconcileNameMaps(
        UniFiDeviceResponse device,
        Dictionary<(string DeviceMac, string IfName), InterfaceNameMap> existing,
        NetworkOptimizerDbContext db)
    {
        var mac = NormalizeMac(device.Mac);
        foreach (var port in device.PortTable ?? new List<SwitchPort>())
        {
            if (port.PortIdx <= 0) continue;
            var ifName = IfNameFor(port);
            var key = (mac, ifName);
            if (!existing.TryGetValue(key, out var row))
            {
                row = new InterfaceNameMap { DeviceMac = mac, IfName = ifName };
                db.InterfaceNameMaps.Add(row);
                existing[key] = row;
            }
            row.IfIndex = port.PortIdx;
            row.IfAlias = port.Name;
            row.PortNumber = port.PortIdx;
            row.FriendlyName = string.IsNullOrWhiteSpace(port.Name) ? null : port.Name.Trim();
            if (port.Speed > 0) row.SpeedMbps = port.Speed;
            if (port.SfpFound.HasValue) row.IsSfp = port.SfpFound;
            row.LastUpdated = DateTime.UtcNow;
        }
    }

    private static string NormalizeMac(string mac) => mac.ToLowerInvariant().Replace("-", ":");
}
