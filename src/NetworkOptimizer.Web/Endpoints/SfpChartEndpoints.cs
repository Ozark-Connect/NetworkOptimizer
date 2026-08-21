using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Authorization;

namespace NetworkOptimizer.Web.Endpoints;

public static class SfpChartEndpoints
{
    public static void Map(WebApplication app)
    {
        // Gate 2 (design doc 06): the whole group carries authorization metadata, which is what
        // architecture test A1 checks. The policy short-circuits when the install has
        // authentication disabled (GlobalRoleHandler).
        var group = app.MapGroup("").RequireAuthorization(Policies.RequireViewer);

        group.MapGet("/api/monitoring/sfp-chart", async (
            MonitoringInfluxClient influx,
            SiteDbContextFactory siteDbFactory,
            SiteContextService siteContext,
            int? rangeHours,
            DateTime? from,
            DateTime? to,
            CancellationToken ct) =>
        {
            DateTime queryFrom, queryTo;
            if (from.HasValue && to.HasValue)
            {
                queryFrom = from.Value.ToUniversalTime();
                queryTo = to.Value.ToUniversalTime();
            }
            else
            {
                var hours = rangeHours ?? 1;
                queryTo = DateTime.UtcNow;
                queryFrom = hours == 0 ? queryTo.AddMinutes(-15) : queryTo.AddHours(-hours);
            }

            await using var db = siteDbFactory.CreateForSite(siteContext.Slug, siteContext.IsDefault);
            var sfps = await db.MonitoredSfps.AsNoTracking()
                .Where(s => s.IsMonitoredOnt)
                .OrderBy(s => s.DeviceMac).ThenBy(s => s.PortName)
                .ToListAsync(ct);

            if (sfps.Count == 0)
                return Results.Ok(new { modules = Array.Empty<object>(), events = Array.Empty<object>() });

            var modules = sfps.Select(s => (s.DeviceMac, s.PortName)).ToList();
            var data = await influx.QuerySfpByModulesAsync(modules, queryFrom, queryTo, ct: ct);
            // Supplemental PON-layer series (attached ONT configs); empty for modules without one.
            var ponData = await influx.QuerySfpPonByModulesAsync(modules, queryFrom, queryTo, ct: ct);

            var targets = await db.MonitoringTargets.AsNoTracking()
                .Where(t => t.TargetType == MonitoringTargetType.Fabric && t.RetiredAt == null)
                .Select(t => new { t.DeviceMac, t.Name })
                .ToListAsync(ct);
            var nameMap = targets
                .Where(t => !string.IsNullOrEmpty(t.DeviceMac))
                .GroupBy(t => t.DeviceMac!.Replace("-", ":").ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.First().Name);

            var result = sfps.Select(s =>
            {
                var key = $"{s.DeviceMac.Replace("-", ":").ToLowerInvariant()}:{s.PortName}";
                data.TryGetValue(key, out var points);
                var pts = points ?? new List<MonitoringInfluxClient.SfpPoint>();
                var deviceName = nameMap.TryGetValue(
                    s.DeviceMac.Replace("-", ":").ToLowerInvariant(), out var n) ? n : s.DeviceMac;
                var label = !string.IsNullOrEmpty(s.FriendlyName)
                    ? s.FriendlyName
                    : $"{deviceName} port {s.PortName}";

                return new
                {
                    id = key,
                    label,
                    category = (int)s.Category,
                    sfpPart = s.SfpPart,
                    data = pts.Select(p => new
                    {
                        time = p.Time.ToString("o"),
                        rx = p.RxPowerDbm,
                        tx = p.TxPowerDbm,
                        temp = p.TemperatureC,
                        voltage = p.VoltageV
                    }),
                    pon = BuildPonSeries(ponData.TryGetValue(key, out var ponPts) ? ponPts : null)
                };
            });

            // Grouped rather than ToDictionary'd: a duplicate module row would otherwise throw
            // out of the whole response, series included, for the sake of the marks.
            var modulesById = sfps
                .GroupBy(s => ModuleKey(s.DeviceMac, s.PortName))
                .ToDictionary(g => g.Key, g => (
                    Port: g.First().PortName,
                    Device: nameMap.TryGetValue(
                        g.First().DeviceMac.Replace("-", ":").ToLowerInvariant(), out var dn)
                            ? dn
                            : g.First().DeviceMac));

            var events = await BuildSfpEventsAsync(db, modulesById, queryFrom, queryTo, ct);

            return Results.Ok(new { modules = result, events });
        });
    }

    /// <summary>
    /// The SFP alerts, and only those. A restart or a device-wide alert belongs to the device
    /// rather than to any one module, so it is left to Device Stats instead of being repeated
    /// against every module the device happens to carry.
    /// </summary>
    private static readonly HashSet<string> SfpEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "monitoring.sfp_temperature",
        "monitoring.sfp_rx_power",
        "monitoring.sfp_tx_power",
    };

    /// <summary>
    /// The exception to that: a PON link going down is the one ONT condition worth seeing while
    /// reading the optics, since it is what the RX and TX traces are usually being checked
    /// against.
    /// </summary>
    private const string PonLinkDownEventType = "ont.pon_link_down";

    /// <summary>Marks every chart on the tab.</summary>
    private const string ScopeAll = "all";

    /// <summary>Marks the PON charts alone.</summary>
    private const string ScopePon = "pon";

    /// <summary>Module identity used by both the series and the marks: device MAC plus port.</summary>
    private static string ModuleKey(string deviceMac, string portName) =>
        $"{deviceMac.Replace("-", ":").ToLowerInvariant()}:{portName}";

    /// <summary>
    /// The module an ONT alert belongs to, read from the link the alert already carries.
    ///
    /// ONT alerts identify themselves by ont_id, which says nothing about which SFP the ONT is
    /// attached to - but the collection agent sets SourceUrl to that module's deep link precisely
    /// because attached ONTs surface on SFP Stats. That also makes this the right discriminator
    /// rather than a shortcut: a standalone ONT gets a <c>tab=ont</c> link instead and is left
    /// alone, which is what should happen on a tab about SFP modules.
    /// </summary>
    private static string? ModuleKeyFromSourceUrl(string? sourceUrl)
    {
        if (string.IsNullOrEmpty(sourceUrl)) return null;

        var queryStart = sourceUrl.IndexOf('?');
        if (queryStart < 0) return null;

        foreach (var pair in sourceUrl[(queryStart + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0 || pair[..eq] != "sfp") continue;

            // Compared verbatim, NOT lower-cased: the agent builds this value with the same
            // expression ModuleKey uses, which lower-cases the MAC but leaves the port name as
            // the device spells it. Lower-casing the whole thing here would silently stop
            // matching the moment a port name carried an upper-case character.
            var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            return string.IsNullOrEmpty(value) ? null : value;
        }

        return null;
    }


    /// <summary>
    /// Marks for the charted modules over the same window as the series.
    ///
    /// An SFP alert names its module in the event context rather than in a column, so the match
    /// is made there: DeviceId alone would put a mark on every module of a multi-SFP device.
    /// </summary>
    private static async Task<List<object>> BuildSfpEventsAsync(
        NetworkOptimizerDbContext db,
        Dictionary<string, (string Port, string Device)> modulesById,
        DateTime from,
        DateTime to,
        CancellationToken ct)
    {
        // Time carried alongside the payload so the list can be ordered without reflecting over
        // the anonymous type; the payload stays anonymous to keep the JSON names literal, the way
        // the rest of this endpoint's response is built.
        var events = new List<(DateTime At, object Payload)>();
        if (modulesById.Count == 0) return new List<object>();

        var alerts = await db.AlertHistory.AsNoTracking()
            .Where(a => a.ContextJson != null && a.TriggeredAt >= from && a.TriggeredAt <= to
                && SfpEventTypes.Contains(a.EventType))
            .Select(a => new { a.EventType, a.Severity, a.Title, a.Message, a.ContextJson, a.TriggeredAt })
            .ToListAsync(ct);

        foreach (var alert in alerts)
        {
            string? deviceMac = null, portName = null;
            try
            {
                var context = JsonSerializer.Deserialize<Dictionary<string, string>>(alert.ContextJson!);
                context?.TryGetValue("device_mac", out deviceMac);
                context?.TryGetValue("port_name", out portName);
            }
            catch (JsonException)
            {
                // A context we cannot read costs one mark, not the whole tab.
                continue;
            }

            if (string.IsNullOrEmpty(deviceMac) || string.IsNullOrEmpty(portName)) continue;

            var key = ModuleKey(deviceMac, portName);
            if (!modulesById.TryGetValue(key, out var module)) continue;

            // Written as UtcNow but read back from SQLite as Unspecified, and "o" on an
            // Unspecified value emits no zone - which the browser then reads as local time.
            var triggeredAtUtc = DateTime.SpecifyKind(alert.TriggeredAt, DateTimeKind.Utc);

            events.Add((triggeredAtUtc, new
            {
                key,
                scope = ScopeAll,
                device = module.Device,
                port = module.Port,
                time = triggeredAtUtc.ToString("o"),
                kind = "alert",
                severity = ChartEventMarks.Severity(alert.Severity),
                title = SfpEventLabel(alert.EventType, alert.Title),
                detail = TrimTrailingLocation(alert.Message, module.Device, module.Port),
            }));
        }

        var ontAlerts = await db.AlertHistory.AsNoTracking()
            .Where(a => a.SourceUrl != null && a.TriggeredAt >= from && a.TriggeredAt <= to
                && ChartEventMarks.OntEventTypes.Contains(a.EventType))
            .Select(a => new { a.EventType, a.Severity, a.Title, a.Message, a.DeviceName, a.SourceUrl, a.TriggeredAt })
            .ToListAsync(ct);

        foreach (var alert in ontAlerts)
        {
            var key = ModuleKeyFromSourceUrl(alert.SourceUrl);
            if (key is null || !modulesById.TryGetValue(key, out var module)) continue;

            var triggeredAtUtc = DateTime.SpecifyKind(alert.TriggeredAt, DateTimeKind.Utc);

            events.Add((triggeredAtUtc, new
            {
                key,
                scope = alert.EventType.Equals(PonLinkDownEventType, StringComparison.OrdinalIgnoreCase)
                    ? ScopeAll
                    : ScopePon,
                device = module.Device,
                port = module.Port,
                time = triggeredAtUtc.ToString("o"),
                kind = "alert",
                severity = ChartEventMarks.Severity(alert.Severity),
                title = ChartEventMarks.OntEventLabel(alert.Title, alert.DeviceName),
                detail = alert.Message,
            }));
        }

        // The mark layer folds by proximity, which assumes time order.
        return events.OrderBy(e => e.At).Select(e => e.Payload).ToList();
    }

    /// <summary>
    /// Short label for a mark. The stored title spells out the device and port, which the
    /// tooltip already carries in its own rows, so the mark names the condition alone.
    /// </summary>
    private static string SfpEventLabel(string eventType, string title) => eventType switch
    {
        "monitoring.sfp_temperature" => "High temperature",
        "monitoring.sfp_rx_power" => "RX power low",
        "monitoring.sfp_tx_power" => "TX power high",
        _ => title,
    };

    /// <summary>
    /// Drops the trailing "on &lt;device&gt; port &lt;n&gt;" the alert message ends with, since the
    /// tooltip states both on their own rows directly above it.
    /// </summary>
    private static string TrimTrailingLocation(string message, string device, string port)
    {
        foreach (var suffix in new[] { $" on {device} port {port}", $" on port {port}" })
        {
            if (message.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return message[..^suffix.Length];
        }
        return message;
    }

    /// <summary>
    /// Project supplemental PON points into the chart payload, converting cumulative
    /// counters to per-interval deltas (CM Stats style - cumulative lines are unreadable).
    /// A negative step means the ONT rebooted and reset its counters; that interval's
    /// delta is null (a gap) rather than a bogus spike. Null when the module has no
    /// supplemental data, so the UI can hide the PON section entirely.
    /// </summary>
    private static List<object>? BuildPonSeries(List<MonitoringInfluxClient.SfpPonPoint>? points)
    {
        if (points is not { Count: > 0 }) return null;

        static long? Delta(long? cur, long? prev) =>
            cur is long c && prev is long p && c >= p ? c - p : null;

        var ordered = points.OrderBy(p => p.Time).ToList();
        var items = new List<object>(ordered.Count);
        MonitoringInfluxClient.SfpPonPoint? prev = null;
        foreach (var p in ordered)
        {
            items.Add(new
            {
                time = p.Time.ToString("o"),
                state = p.PonLinkStatus,
                statePrev = p.PonLinkStatusPrev,
                onuId = p.OnuId,
                dsFec = p.DsFecEnabled,
                usFec = p.UsFecEnabled,
                respTime = p.OnuResponseTime,
                uptime = p.SfpUptimeS,
                lanLink = p.LanLinkStatus,
                lanMode = p.LanMode,
                bip = Delta(p.BipErrors, prev?.BipErrors),
                fec = Delta(p.FecErrors, prev?.FecErrors),
                fecCorr = Delta(p.FecCorrectedWords, prev?.FecCorrectedWords),
                hec = Delta(p.HecUncorrected, prev?.HecUncorrected),
                hecCorr = Delta(p.HecCorrected, prev?.HecCorrected),
                bwmapCorr = Delta(p.BwmapCorrected, prev?.BwmapCorrected),
                bwmapUncorr = Delta(p.BwmapUncorrected, prev?.BwmapUncorrected),
                gemTx = Delta(p.GemTxFrames, prev?.GemTxFrames),
                gemTxIdle = Delta(p.GemTxIdleFrames, prev?.GemTxIdleFrames),
                gemRx = Delta(p.GemRxFrames, prev?.GemRxFrames),
                gemDrop = Delta(p.GemRxDropped, prev?.GemRxDropped),
                allocLost = Delta(p.AllocLost, prev?.AllocLost),
                lanFcs = Delta(p.LanRxFcsErrors, prev?.LanRxFcsErrors),
                lanDrop = Delta(p.LanTxDropEvents, prev?.LanTxDropEvents),
                lanOvfl = Delta(p.LanBufferOverflow, prev?.LanBufferOverflow),
            });
            prev = p;
        }
        return items;
    }
}
