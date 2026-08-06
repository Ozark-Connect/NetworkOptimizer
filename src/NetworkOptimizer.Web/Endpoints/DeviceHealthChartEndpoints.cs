using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Authorization;
using NetworkOptimizer.Web.Services.Monitoring;
using NetworkOptimizer.Web.Services.Monitoring.RebootReason;

namespace NetworkOptimizer.Web.Endpoints;

public static class DeviceHealthChartEndpoints
{
    public static void Map(WebApplication app)
    {
        // Gate 2 (design doc 06): the whole group carries authorization metadata, which is what
        // architecture test A1 checks. The policy short-circuits when the install has
        // authentication disabled (GlobalRoleHandler).
        var group = app.MapGroup("").RequireAuthorization(Policies.RequireViewer);

        group.MapGet("/api/monitoring/device-health-chart", async (
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
            var targets = await db.MonitoringTargets.AsNoTracking()
                .Where(t => t.TargetType == MonitoringTargetType.Fabric)
                .OrderBy(t => t.Name)
                .Select(t => new { t.TargetId, t.Name, t.DeviceMac })
                .ToListAsync(ct);

            if (targets.Count == 0)
                return Results.Ok(new { devices = Array.Empty<object>(), customFields = Array.Empty<object>() });

            var deviceMacs = targets
                .Where(t => !string.IsNullOrEmpty(t.DeviceMac))
                .Select(t => t.DeviceMac!)
                .ToList();

            var customOidConfigs = await db.CustomOidConfigurations
                .Where(c => c.Enabled && c.Scope == Storage.Models.CustomOidScope.DeviceLevel
                    && deviceMacs.Contains(c.DeviceMac))
                .ToListAsync(ct);

            var customFieldDefs = customOidConfigs
                .GroupBy(c => c.FieldName)
                .Select(g => new { fieldName = g.Key, description = g.First().Description ?? g.Key })
                .ToList();

            var customFieldNames = customFieldDefs.Select(f => f.fieldName).ToList();

            var result = new List<object>();
            foreach (var t in targets)
            {
                if (string.IsNullOrEmpty(t.DeviceMac)) continue;
                var points = await influx.QueryDeviceHealthAsync(t.DeviceMac, queryFrom, queryTo, ct: ct);

                Dictionary<string, List<(DateTime Time, double Value)>>? customData = null;
                var deviceCustomFields = customOidConfigs
                    .Where(c => c.DeviceMac == t.DeviceMac)
                    .Select(c => c.FieldName)
                    .Distinct()
                    .ToList();
                if (deviceCustomFields.Count > 0)
                    customData = await influx.QueryCustomOidFieldsAsync(
                        t.DeviceMac, deviceCustomFields, queryFrom, queryTo, ct: ct);

                result.Add(new
                {
                    name = t.Name,
                    mac = t.DeviceMac,
                    data = points.Select(p => new
                    {
                        time = p.Time.ToString("o"),
                        cpu = p.CpuPercent,
                        mem = p.MemoryUsedPercent,
                        temp = p.TemperatureC
                    }),
                    custom = customData?.ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value.Select(v => new { time = v.Time.ToString("o"), value = v.Value }))
                });
            }

            var events = await BuildAnnotationsAsync(influx, db, targets
                .Where(t => !string.IsNullOrEmpty(t.DeviceMac))
                .ToDictionary(t => NormalizeMac(t.DeviceMac!), t => t.DeviceMac!),
                queryFrom, queryTo, ct);

            return Results.Ok(new { devices = result, customFields = customFieldDefs, events });
        });
    }

    /// <summary>
    /// Alert event types that are already covered by a richer source and must not be annotated twice.
    /// The reboot alert and the Influx reboot record describe the same restart, but only the Influx
    /// record carries the category, the evidence and the firmware - and it is written for every boot,
    /// not just the ones recent enough to alert on.
    /// </summary>
    private static readonly HashSet<string> AlertTypesCoveredElsewhere =
        new(StringComparer.OrdinalIgnoreCase) { DeviceRebootAlertEvaluator.RebootEventType };

    /// <summary>
    /// Reboot categories the operator did not ask for. Mirrors DeviceRebootReason.IsUnexpected,
    /// which lives in the Web services tier and works on the enum rather than the stored string.
    /// </summary>
    private static readonly HashSet<string> UnexpectedRebootCategories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            nameof(RebootCategory.PowerLoss), nameof(RebootCategory.AbruptStop),
            nameof(RebootCategory.KernelPanic), nameof(RebootCategory.HardwareHang),
            nameof(RebootCategory.Watchdog),
        };

    /// <summary>
    /// The events worth marking on a device's health charts, over the same window as the series.
    /// </summary>
    /// <param name="influx">Time-series client, source of the reboot records.</param>
    /// <param name="db">Site database, source of the device-scoped alert history.</param>
    /// <param name="macsByNormalized">
    /// Charted devices, keyed by normalized MAC so both sources can be matched against them, with
    /// the target's own MAC spelling as the value - that is the key the chart filters on.
    /// </param>
    /// <param name="from">Window start.</param>
    /// <param name="to">Window end.</param>
    /// <param name="ct">Cancellation token.</param>
    private static async Task<List<object>> BuildAnnotationsAsync(
        MonitoringInfluxClient influx,
        NetworkOptimizerDbContext db,
        Dictionary<string, string> macsByNormalized,
        DateTime from,
        DateTime to,
        CancellationToken ct)
    {
        // Carried alongside the payload so the two sources can be merged into one time-ordered
        // list; the payload itself stays an anonymous object to keep the JSON property names
        // literal, the way the rest of this endpoint's response is built.
        var events = new List<(DateTime At, object Payload)>();
        if (macsByNormalized.Count == 0) return new List<object>();

        // Reboots. Influx holds one record per boot in the long-term bucket, so this reaches as
        // far back as the chart's 30d preset does.
        var reboots = await influx.QueryDeviceRebootsInRangeAsync(from, to, ct);
        foreach (var reboot in reboots)
        {
            if (!macsByNormalized.TryGetValue(NormalizeMac(reboot.DeviceMac), out var mac)) continue;

            var unexpected = UnexpectedRebootCategories.Contains(reboot.Category);
            events.Add((reboot.BootedAt, new
            {
                mac,
                time = reboot.BootedAt.ToString("o"),
                kind = "reboot",
                severity = unexpected ? "warning" : "info",
                title = string.IsNullOrWhiteSpace(reboot.Summary) ? "Restarted" : reboot.Summary,
                detail = string.IsNullOrWhiteSpace(reboot.FirmwareVersion)
                    ? reboot.Detail
                    : string.IsNullOrWhiteSpace(reboot.Detail)
                        ? $"Firmware {reboot.FirmwareVersion}"
                        : $"{reboot.Detail}. Firmware {reboot.FirmwareVersion}",
            }));
        }

        // Device-scoped alerts. DeviceId holds a MAC for the device evaluators but an unrelated
        // key for others ("all-wans", a WAN key, "network-audit"), and the MAC spelling is
        // whatever the publishing evaluator had. Narrowing in SQL to the spellings a charted
        // device could plausibly be stored under keeps a noisy 30d window from being pulled into
        // memory just to be discarded; the normalized compare below is still the decider.
        var macCandidates = macsByNormalized
            .SelectMany(kvp => new[]
            {
                kvp.Value, kvp.Value.ToLowerInvariant(), kvp.Value.ToUpperInvariant(),
                kvp.Key, kvp.Key.ToUpperInvariant(),
            })
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var alerts = await db.AlertHistory.AsNoTracking()
            .Where(a => a.DeviceId != null && macCandidates.Contains(a.DeviceId)
                && a.TriggeredAt >= from && a.TriggeredAt <= to)
            .Select(a => new { a.EventType, a.Severity, a.Title, a.Message, a.DeviceId, a.TriggeredAt })
            .ToListAsync(ct);

        foreach (var alert in alerts)
        {
            if (AlertTypesCoveredElsewhere.Contains(alert.EventType)) continue;
            if (!macsByNormalized.TryGetValue(NormalizeMac(alert.DeviceId!), out var mac)) continue;

            events.Add((alert.TriggeredAt, new
            {
                mac,
                time = alert.TriggeredAt.ToString("o"),
                kind = "alert",
                severity = alert.Severity switch
                {
                    AlertSeverity.Critical or AlertSeverity.Error => "critical",
                    AlertSeverity.Warning => "warning",
                    _ => "info",
                },
                // Titles carry the device name and a site suffix, which the chart already knows
                // from the series it sits on; the event type is what distinguishes one mark
                // from the next.
                title = AlertLabel(alert.EventType, alert.Title),
                detail = alert.Message,
            }));
        }

        return events.OrderBy(e => e.At).Select(e => e.Payload).ToList();
    }

    /// <summary>
    /// Short label for an alert mark: the alert's own noun, without the device name the chart
    /// already shows. Falls back to the stored title when the event type is not one we name.
    /// </summary>
    private static string AlertLabel(string eventType, string title) => eventType switch
    {
        "device.offline" => "Offline",
        "device.recovered" => "Recovered",
        "device.gateway_high_cpu" => "High CPU",
        "device.gateway_high_memory" => "High memory",
        "device.high_temperature" => "High temperature",
        _ => title,
    };

    /// <summary>Strips MAC separators and case so spellings from different sources compare equal.</summary>
    private static string NormalizeMac(string mac) =>
        mac.Replace(":", "").Replace("-", "").ToLowerInvariant();
}
