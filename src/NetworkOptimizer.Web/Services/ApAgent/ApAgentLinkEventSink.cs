using System.Globalization;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Stores the AP Agent's LAN Health events for one site: the access point's own wired link going
/// down and up (kernel PHY lines, exact to the second), the uplink's carrier and CRC counters, and
/// the log lines that explain a relink. Link events go to <c>events</c>; counters go to the port's
/// agent series on <c>interface_counters</c>. LAN Health reads both.
/// </summary>
public sealed class ApAgentLinkEventSink
{
    /// <summary>The <c>event_type</c> values this sink writes, prefixed so they never collide with other writers.</summary>
    public static class EventTypes
    {
        public const string LinkDown = "ap_link_down";
        public const string LinkUp = "ap_link_up";
        public const string UplinkReconfigure = "ap_uplink_reconfigure";
        public const string UplinkLost = "ap_uplink_lost";
        public const string InformFailed = "ap_inform_failed";
    }

    /// <summary>The <c>port_id</c> of the agent's counter series for an interface.</summary>
    public static string PortIdFor(string iface) => $"apagent:{iface}";

    private readonly MonitoringInfluxRegistry _influx;
    private readonly string _siteSlug;

    /// <summary>Creates the sink for one site.</summary>
    public ApAgentLinkEventSink(MonitoringInfluxRegistry influx, string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _influx = influx;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
    }

    /// <summary>True for the event kinds this sink takes, so the roam collector routes them here.</summary>
    public static bool Handles(string type) => type is ApAgentEventTypes.LinkDown or ApAgentEventTypes.LinkUp
        or ApAgentEventTypes.UplinkCounters or ApAgentEventTypes.UplinkReconfigure or ApAgentEventTypes.UplinkLost
        or ApAgentEventTypes.InformFailed;

    /// <summary>Stores one event from an access point's ring.</summary>
    public void Record(string apMac, string? apName, ApAgentEvent e)
    {
        var influx = _influx.GetFor(_siteSlug);
        if (!influx.IsConfigured) return;
        var mac = apMac.Trim().ToLowerInvariant();
        var at = e.At;
        switch (e.Type)
        {
            case ApAgentEventTypes.LinkDown:
                _ = influx.WriteEventAsync(mac, EventTypes.LinkDown, "warning", apName, e.Iface, null, null, at);
                break;
            case ApAgentEventTypes.LinkUp:
                _ = influx.WriteEventAsync(mac, EventTypes.LinkUp, "info", apName, e.Iface, null, e.Detail, at);
                break;
            case ApAgentEventTypes.UplinkReconfigure:
                _ = influx.WriteEventAsync(mac, EventTypes.UplinkReconfigure, "info", e.Detail, e.Iface, null, null, at);
                break;
            case ApAgentEventTypes.UplinkLost:
                _ = influx.WriteEventAsync(mac, EventTypes.UplinkLost, "warning", apName, e.Iface, null, null, at);
                break;
            case ApAgentEventTypes.InformFailed:
                _ = influx.WriteEventAsync(mac, EventTypes.InformFailed, "warning", apName, null, null, null, at);
                break;
            case ApAgentEventTypes.UplinkCounters when e.Counters != null && !string.IsNullOrEmpty(e.Iface):
                var fields = new List<KeyValuePair<string, object>>();
                if (e.Counters.CarrierChanges is long cc) fields.Add(new(InfluxFieldNames.UplinkCarrierChanges, cc));
                if (e.Counters.RxCrcErrors is long crc) fields.Add(new(InfluxFieldNames.UplinkRxCrcErrors, crc));
                _ = influx.WritePortSnapshotAsync(mac, e.Iface!, PortIdFor(e.Iface!), fields, at);
                break;
        }
    }

    /// <summary>The speed in Mbps a link_up event's detail carries, or null.</summary>
    public static int? SpeedOf(string? detail) =>
        int.TryParse(detail, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
}
