using System.Collections.Concurrent;
using System.Globalization;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.Monitoring.LanHealth;

/// <summary>
/// Records what the UniFi Console knows about every wired port and every uplink, once per fresh
/// device snapshot: link speed and duplex, the switch's own link-down count, STP changes, the port
/// table's errors and packets, PoE per port and per device, and mesh uplink signal and rates. It
/// also records each device's administrative state changes (Upgrading, Provisioning, ...) as
/// <c>events</c>, so LAN Health can explain loss inside those windows. Additive fields only.
/// </summary>
public sealed class LanPortSnapshotRecorder
{
    /// <summary>Event types for device state windows, in the <c>events</c> measurement.</summary>
    public const string StateEventType = "device_state";

    private readonly ConcurrentDictionary<string, long> _lastTakenSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _lastState = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The console series' <c>port_id</c> for a port. Gateways keep the ifname, so their link speed
    /// history stays on the series it was always written to.
    /// </summary>
    public static string PortIdFor(UniFiDeviceResponse device, SwitchPort port) =>
        device.DeviceType == DeviceType.Gateway && !string.IsNullOrWhiteSpace(port.IfName)
            ? port.IfName.Trim()
            : $"pt:{port.PortIdx}";

    /// <summary>The console series' <c>if_name</c>: the kernel name where the console gives one, else "Port N".</summary>
    public static string IfNameFor(SwitchPort port) =>
        !string.IsNullOrWhiteSpace(port.IfName) ? port.IfName.Trim() : $"Port {port.PortIdx}";

    /// <summary>Writes every device with a new snapshot. Returns the number of ports written.</summary>
    public int Record(IEnumerable<UniFiDeviceResponse> devices, MonitoringInfluxClient influx, DateTime now)
    {
        var written = 0;
        foreach (var device in devices)
        {
            if (!device.Adopted || string.IsNullOrEmpty(device.Mac)) continue;
            var mac = device.Mac.Trim().ToLowerInvariant();

            RecordState(mac, device, influx, now);

            // A repeated last_seen is a cached list or a device that stopped informing: not a new reading.
            if (device.LastSeen <= 0 || !UniFiDeviceStateMap.IsOnline(device.State)) continue;
            if (_lastTakenSeen.TryGetValue(mac, out var taken) && device.LastSeen <= taken) continue;
            _lastTakenSeen[mac] = device.LastSeen;
            if (!influx.IsConfigured) continue;

            foreach (var port in device.PortTable ?? [])
            {
                if (port.PortIdx <= 0) continue;
                var fields = PortFields(device, port);
                if (fields.Count == 0) continue;
                _ = influx.WritePortSnapshotAsync(mac, IfNameFor(port), PortIdFor(device, port), fields, now);
                written++;
            }

            var deviceFields = DeviceFields(device);
            if (deviceFields.Count > 0)
                _ = influx.WriteDeviceFieldsAsync(mac, MonitoringCollectionAgent.DescribeDeviceType(device.DeviceType), deviceFields, now);
        }
        return written;
    }

    /// <summary>The fields one port contributes. Gateways skip speed and duplex: the gateway link speed pass writes those.</summary>
    internal static List<KeyValuePair<string, object>> PortFields(UniFiDeviceResponse device, SwitchPort port)
    {
        var f = new List<KeyValuePair<string, object>>();
        void Add(string name, object? value) { if (value != null) f.Add(new(name, value)); }

        var up = port.Up && port.Speed > 0;
        Add(InfluxFieldNames.LinkUp, port.Up);
        Add(InfluxFieldNames.LinkAutoneg, port.Autoneg);
        if (device.DeviceType != DeviceType.Gateway && up)
        {
            Add(InfluxFieldNames.LinkSpeedMbps, (long)port.Speed);
            Add(InfluxFieldNames.LinkFullDuplex, port.FullDuplex);
        }
        Add(InfluxFieldNames.LinkDownCount, port.LinkDownCount);
        if (port.StpStateChangeCount is { Count: > 0 } stp)
            Add(InfluxFieldNames.StpStateChangeCount, stp.Sum(s => s.ChangeCount));
        Add(InfluxFieldNames.PortRxErrors, port.RxErrors);
        Add(InfluxFieldNames.PortTxErrors, port.TxErrors);
        Add(InfluxFieldNames.PortRxDropped, port.RxDropped);
        Add(InfluxFieldNames.PortTxDropped, port.TxDropped);
        if (port.RxPackets > 0) Add(InfluxFieldNames.PortRxPackets, port.RxPackets);
        if (port.TxPackets > 0) Add(InfluxFieldNames.PortTxPackets, port.TxPackets);
        Add(InfluxFieldNames.PortRxBroadcast, port.RxBroadcast);
        Add(InfluxFieldNames.PortRxMulticast, port.RxMulticast);
        if (port.MacTableCount is int macs) Add(InfluxFieldNames.MacTableCount, (long)macs);
        if (port.LastConnection?.ConnectedAt is long connectedAt && connectedAt > 0) Add(InfluxFieldNames.ConnectedAt, connectedAt);

        // PoE: only on a port that can source it, so a non-PoE port never reads as 0 W.
        if (port.PortPoe)
        {
            Add(InfluxFieldNames.PoeMode, port.PoeMode);
            Add(InfluxFieldNames.PoePower, ParseDouble(port.PoePower));
            Add(InfluxFieldNames.PoeCurrent, ParseDouble(port.PoeCurrent));
            Add(InfluxFieldNames.PoeVoltage, ParseDouble(port.PoeVoltage));
            Add(InfluxFieldNames.PoeClass, PoeClassNumber(port.PoeClass));
            Add(InfluxFieldNames.PoeGood, port.PoeGood);
        }
        return f;
    }

    /// <summary>Device PoE totals and the device's own uplink as it reports it (mesh signal and rates, every MLO member).</summary>
    internal static List<KeyValuePair<string, object>> DeviceFields(UniFiDeviceResponse device)
    {
        var f = new List<KeyValuePair<string, object>>();
        void Add(string name, object? value) { if (value != null) f.Add(new(name, value)); }

        Add(InfluxFieldNames.PoeTotalUsedPower, device.TotalUsedPower);
        Add(InfluxFieldNames.PoeTotalMaxPower, device.TotalMaxPower);
        Add(InfluxFieldNames.PoeTotalMaxEffectivePower, device.TotalMaxEffectivePower);
        if (device.DeviceNearPowerLimit is { } near || device.DeviceNearEffectivePowerLimit is { })
            Add(InfluxFieldNames.PoeNearLimit, device.DeviceNearPowerLimit == true || device.DeviceNearEffectivePowerLimit == true);

        var uplink = device.Uplink;
        if (uplink != null && device.DeviceType != DeviceType.Gateway)
        {
            if (!string.IsNullOrWhiteSpace(uplink.UplinkMac)) Add(InfluxFieldNames.UplinkParentMac, uplink.UplinkMac.Trim().ToLowerInvariant());
            if (device.UplinkDepth is int depth) Add(InfluxFieldNames.UplinkDepth, (long)depth);
            if (string.Equals(uplink.Type, "wireless", StringComparison.OrdinalIgnoreCase))
            {
                if (uplink.Signal is int s) Add(InfluxFieldNames.UplinkSignalDbm, (long)s);
                if (uplink.Noise is int n) Add(InfluxFieldNames.UplinkNoiseDbm, (long)n);
                if (uplink.TxRate > 0) Add(InfluxFieldNames.UplinkTxRateKbps, uplink.TxRate);
                if (uplink.RxRate > 0) Add(InfluxFieldNames.UplinkRxRateKbps, uplink.RxRate);
                var members = (uplink.MloLinks ?? []).OrderBy(m => BandOrder(m.Radio)).ToList();
                Add(InfluxFieldNames.UplinkMloLinkCount, (long)members.Count);
                for (var i = 0; i < members.Count; i++)
                {
                    var m = members[i];
                    if (m.Signal is int ms) Add(InfluxFieldNames.UplinkMloField(i, "signal_dbm"), (long)ms);
                    if (m.TxRate > 0) Add(InfluxFieldNames.UplinkMloField(i, "tx_rate_kbps"), m.TxRate);
                    if (m.RxRate > 0) Add(InfluxFieldNames.UplinkMloField(i, "rx_rate_kbps"), m.RxRate);
                    if (m.Channel is int ch) Add(InfluxFieldNames.UplinkMloField(i, "channel"), (long)ch);
                    if (!string.IsNullOrEmpty(m.Radio)) Add(InfluxFieldNames.UplinkMloField(i, "radio"), m.Radio);
                }
            }
        }
        return f;
    }

    /// <summary>A device's administrative state change, stored as an event at the time it was first seen.</summary>
    private void RecordState(string mac, UniFiDeviceResponse device, MonitoringInfluxClient influx, DateTime now)
    {
        var status = UniFiDeviceStateMap.ToStatus(device.State);
        var label = status.Kind == DeviceStatusKind.Transitional ? status.Label : status.IsOnline ? "Online" : status.Label;
        var previous = _lastState.TryGetValue(mac, out var p) ? p : null;
        _lastState[mac] = label;
        // The first sighting of an online device is no change; a first sighting mid-upgrade still opens a window.
        if (previous == label || (previous == null && label == "Online")) return;
        if (!influx.IsConfigured) return;
        _ = influx.WriteEventAsync(mac, StateEventType, "info", device.Name, null, previous, label, now);
    }

    private static int BandOrder(string? radio) => radio switch { "ng" => 0, "na" => 1, "6e" => 2, _ => 3 };

    private static double? ParseDouble(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

    /// <summary>"Class 4" to 4; null when absent or unreadable.</summary>
    internal static long? PoeClassNumber(string? poeClass)
    {
        if (string.IsNullOrWhiteSpace(poeClass)) return null;
        var digits = new string(poeClass.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out var n) ? n : null;
    }
}
