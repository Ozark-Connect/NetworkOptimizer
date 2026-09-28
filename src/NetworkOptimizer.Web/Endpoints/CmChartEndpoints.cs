using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Authorization;

namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// REST endpoint for cable modem aggregate time-series data.
/// Returns DS power, DS SNR, US power, and error counter deltas, plus, for modems that report
/// them, the DOCSIS state, the modem's own event log, and event marks for the charts.
/// </summary>
public static class CmChartEndpoints
{
    /// <summary>Entries of a modem's own log sent for the table, newest first.</summary>
    private const int MaxLogEntries = 50;

    public static void Map(WebApplication app)
    {
        // Gate 2 (design doc 06): the whole group carries authorization metadata, which is what
        // architecture test A1 checks. The policy short-circuits when the install has
        // authentication disabled (GlobalRoleHandler).
        var group = app.MapGroup("").RequireAuthorization(Policies.RequireViewer);

        group.MapGet("/api/monitoring/cm-chart", async (
            MonitoringInfluxClient influx,
            ICableModemService cmService,
            int? rangeHours,
            DateTime? from,
            DateTime? to,
            string? cmId,
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

            var data = await influx.QueryCableModemAsync(queryFrom, queryTo, cmId, ct: ct);

            var configs = await cmService.GetConfigsAsync();
            var nameMap = configs.ToDictionary(c => c.Id.ToString(), c => c.Name);
            var cached = await cmService.GetAllCachedStatsAsync();

            // Marks come from the modem's logged events in the window. Notice/Information lines
            // (profile changes and the like arrive every few minutes) are left to the log table so
            // the marks stay the things worth looking at: recognised events and warnings or worse.
            // Only modems that keep an event log are asked for marks: the rest have none, and the
            // chart polls every few seconds.
            var logging = configs
                .Where(c => c.Provider == NetworkOptimizer.Web.Services.CableModemProviders.Uci.UciInformService.ProviderKey
                            || (cached.TryGetValue(c.Id, out var s) && s.Events.Count > 0))
                .Select(c => c.Id.ToString())
                .ToHashSet();
            var events = new List<object>();
            foreach (var id in data.Keys.Where(k => nameMap.ContainsKey(k) && logging.Contains(k)))
            {
                var logged = await influx.QueryCableModemLogEventsAsync(id, queryFrom, queryTo, ct);
                foreach (var e in logged.Where(e => e.Kind != null || MarkSeverity(e.Level) != "info"))
                {
                    events.Add(new
                    {
                        key = id,
                        time = e.At.ToString("o"),
                        kind = "alert",
                        severity = MarkSeverity(e.Level),
                        title = MarkTitle(e.Kind, e.Level),
                        detail = e.Text,
                        device = nameMap[id],
                    });
                }
            }

            // Only surface modems that still have a config. Deleting a CM config
            // leaves its historical series in InfluxDB; without this filter those
            // orphaned cm_ids show up as phantom "CM {id}" entries on the chart.
            var result = data
                .Where(kvp => nameMap.ContainsKey(kvp.Key))
                .Select(kvp =>
            {
                var name = nameMap[kvp.Key];

                // Current state for the detail table, sent once per modem rather than per point.
                var pts = kvp.Value;
                var stats = int.TryParse(kvp.Key, out var cmIdNum) && cached.TryGetValue(cmIdNum, out var s) ? s : null;

                return new
                {
                    id = kvp.Key,
                    label = name,
                    current = new
                    {
                        lockedDsChannels = pts.Select(p => p.LockedDsChannels).LastOrDefault(v => v != null),
                        lockedUsChannels = pts.Select(p => p.LockedUsChannels).LastOrDefault(v => v != null),
                        docsisMode = stats?.DocsisState?.Mode,
                        docsisState = stats?.DocsisState?.State,
                        reinitReason = stats?.DocsisState?.ReinitReason,
                        firmware = stats?.FirmwareVersion,
                        uptimeSeconds = stats?.UptimeSeconds,
                    },
                    log = (stats?.Events ?? []).AsEnumerable().Reverse().Take(MaxLogEntries).Select(e => new
                    {
                        time = e.Time?.ToString("o"),
                        level = e.Level,
                        text = e.Text,
                    }),
                    data = kvp.Value.Select(p => new
                    {
                        time = p.Time.ToString("o"),
                        dsPower = p.DsPowerAvgDbmv,
                        dsSnr = p.DsSnrAvgDb,
                        usPower = p.UsPowerAvgDbmv,
                        lockedDs = p.LockedDsChannels,
                        lockedUs = p.LockedUsChannels,
                        corrDelta = p.CorrDelta,
                        uncorrDelta = p.UncorrDelta,
                    })
                };
            });

            return Results.Ok(new { devices = result, events });
        });
    }

    /// <summary>DOCSIS log level to the mark layer's severity scale.</summary>
    internal static string MarkSeverity(string? level) => (level ?? "").Trim().ToLowerInvariant() switch
    {
        "emergency" or "alert" or "critical" => "critical",
        "error" or "warning" => "warning",
        _ => "info",
    };

    private static string MarkTitle(string? kind, string? level) => kind switch
    {
        NetworkOptimizer.Monitoring.Models.CmEventKinds.T3Timeout => "T3 timeout",
        NetworkOptimizer.Monitoring.Models.CmEventKinds.T4Timeout => "T4 timeout",
        NetworkOptimizer.Monitoring.Models.CmEventKinds.RangingFailure => "Ranging failure",
        _ => string.IsNullOrWhiteSpace(level) ? "DOCSIS event" : $"DOCSIS {level.Trim()}",
    };
}
