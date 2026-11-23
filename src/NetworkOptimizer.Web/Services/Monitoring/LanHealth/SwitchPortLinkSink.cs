using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services.Monitoring.LanHealth;

/// <summary>
/// Stores an on-gateway agent's switch chip readings for its site's gateway: each port link change
/// as a <c>gw_link_down</c> / <c>gw_link_up</c> event, and the chip's error counters on the port's
/// agent series of <c>interface_counters</c>. The gateway is the site's own; the agent announces the
/// capability only when it runs on one.
/// </summary>
public sealed class SwitchPortLinkSink
{
    /// <summary>Event types written to <c>events</c>.</summary>
    public const string LinkDownEvent = "gw_link_down";
    public const string LinkUpEvent = "gw_link_up";

    /// <summary>The agent counter series' <c>port_id</c> for an interface.</summary>
    public static string PortIdFor(string ifName) => $"swchip:{ifName}";

    private readonly MonitoringInfluxRegistry _influx;
    private readonly SiteConnectionRegistry _connections;
    private readonly ILogger<SwitchPortLinkSink> _logger;

    public SwitchPortLinkSink(MonitoringInfluxRegistry influx, SiteConnectionRegistry connections, ILogger<SwitchPortLinkSink> logger)
    {
        _influx = influx;
        _connections = connections;
        _logger = logger;
    }

    /// <summary>Stores one batch for <paramref name="siteSlug"/>. Returns false when the site's gateway is not known yet.</summary>
    public bool Record(string siteSlug, SwitchPortLinkBatch batch)
    {
        var influx = _influx.GetFor(siteSlug);
        if (!influx.IsConfigured) return true;
        var connection = _connections.GetFor(siteSlug);
        var gateway = connection.CachedDevices?
            .FirstOrDefault(d => d.Type == DeviceType.Gateway && !string.IsNullOrEmpty(d.Mac));
        if (gateway == null)
        {
            // Nothing has read the device list yet: start a read so the next minute's batch lands.
            if (connection.CachedDevices == null && connection.IsConnected) _ = connection.GetDiscoveredDevicesAsync();
            _logger.LogDebug("Switch port batch for {Site} dropped: the gateway is not in the device list yet", siteSlug);
            return false;
        }
        var mac = gateway.Mac.Trim().ToLowerInvariant();
        foreach (var e in batch.Events)
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds(e.TimestampUnixMs).UtcDateTime;
            _ = e.Up
                ? influx.WriteEventAsync(mac, LinkUpEvent, "info", batch.SwitchModel, e.IfName, null, e.SpeedMbps > 0 ? e.SpeedMbps.ToString(System.Globalization.CultureInfo.InvariantCulture) : null, at)
                : influx.WriteEventAsync(mac, LinkDownEvent, "warning", batch.SwitchModel, e.IfName, null, null, at);
        }
        foreach (var c in batch.Counters)
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds(c.TimestampUnixMs).UtcDateTime;
            var fields = new List<KeyValuePair<string, object>>
            {
                new(InfluxFieldNames.ChipRxCrcErrors, c.RxCrcErrors),
                new(InfluxFieldNames.ChipTxCrcErrors, c.TxCrcErrors),
                new(InfluxFieldNames.ChipLateCollisions, c.LateCollisions),
                new(InfluxFieldNames.LinkUp, c.Up),
            };
            if (c.SpeedMbps > 0) fields.Add(new(InfluxFieldNames.LinkSpeedMbps, (long)c.SpeedMbps));
            _ = influx.WritePortSnapshotAsync(mac, c.IfName, PortIdFor(c.IfName), fields, at);
        }
        return true;
    }
}
