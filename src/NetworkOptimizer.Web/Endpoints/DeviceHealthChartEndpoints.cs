using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Authorization;
using NetworkOptimizer.Web.Services.Monitoring;
using NetworkOptimizer.Web.Services.Monitoring.HealthChecks;
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
            HealthCheckTemplateService templateService,
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
                .Where(t => t.TargetType == MonitoringTargetType.Fabric && t.RetiredAt == null)
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

            // Health checks chart below the custom OIDs: one chart per field name, so two devices
            // running the same template share a chart the way custom OIDs do. The threshold and
            // operator come from the first definition of each field; a user who gives two devices
            // different thresholds under one field name sees the first one drawn.
            var healthChecks = await db.HealthCheckDefinitions.AsNoTracking()
                .Where(c => c.Enabled && deviceMacs.Contains(c.DeviceMac))
                .ToListAsync(ct);
            var templates = healthChecks.Count > 0 ? templateService.GetTemplates() : Array.Empty<HealthCheckTemplate>();
            var healthCheckDefs = healthChecks
                .GroupBy(c => c.FieldName)
                .Select(g =>
                {
                    var first = g.First();
                    var template = templates.FirstOrDefault(t => string.Equals(t.Id, first.TemplateId, StringComparison.OrdinalIgnoreCase));
                    return new
                    {
                        fieldName = HealthCheckEvaluation.ValueField(g.Key),
                        name = first.Name,
                        threshold = first.Threshold,
                        op = HealthCheckEvaluation.OperatorSymbol(first.Operator),
                        unit = template?.Unit ?? "",
                    };
                })
                .ToList();

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

                Dictionary<string, List<(DateTime Time, double Value)>>? checkData = null;
                var deviceCheckFields = healthChecks
                    .Where(c => c.DeviceMac == t.DeviceMac)
                    .Select(c => HealthCheckEvaluation.ValueField(c.FieldName))
                    .Distinct()
                    .ToList();
                if (deviceCheckFields.Count > 0)
                    checkData = await influx.QueryCustomOidFieldsAsync(
                        t.DeviceMac, deviceCheckFields, queryFrom, queryTo, ct: ct);

                result.Add(new
                {
                    name = t.Name,
                    mac = t.DeviceMac,
                    data = points.Select(p => new
                    {
                        time = p.Time.ToString("o"),
                        cpu = p.CpuPercent,
                        mem = p.MemoryUsedPercent,
                        temp = p.TemperatureC,
                        fan = p.FanSpeedRpm
                    }),
                    custom = customData?.ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value.Select(v => new { time = v.Time.ToString("o"), value = v.Value })),
                    checks = checkData?.ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value.Select(v => new { time = v.Time.ToString("o"), value = v.Value }))
                });
            }

            // Grouped rather than ToDictionary'd: two Fabric targets can carry the same DeviceMac,
            // and a duplicate key would throw out of the whole response - charts included, not
            // just the marks.
            var macsByNormalized = targets
                .Where(t => !string.IsNullOrEmpty(t.DeviceMac))
                .GroupBy(t => NormalizeMac(t.DeviceMac!))
                .ToDictionary(g => g.Key, g => (Key: g.First().DeviceMac!, Name: g.First().Name));

            var events = await BuildAnnotationsAsync(influx, db, macsByNormalized, queryFrom, queryTo, ct);

            return Results.Ok(new { devices = result, customFields = customFieldDefs, healthChecks = healthCheckDefs, events });
        });
    }

    /// <summary>
    /// Alert event types that are already covered by a richer source and must not be annotated twice.
    /// The reboot alert and the Influx reboot record describe the same restart, but only the Influx
    /// record carries the category, the evidence and the firmware - and it is written for every boot,
    /// not just the ones recent enough to alert on.
    /// </summary>
    private static readonly HashSet<string> AlertTypesCoveredElsewhere =
        new(StringComparer.OrdinalIgnoreCase) { DeviceRebootAlertEvaluator.RebootEventType, HealthCheckAlertTypes.Action };

    /// <summary>Short label for a health check remedy mark: the check, then what it did.</summary>
    internal static string ActionLabel(string checkName, string remedy)
    {
        var did = remedy switch
        {
            "RestartService" => "restarted service",
            "KillProcess" => "killed process",
            "RebootDevice" => "rebooted",
            _ => "action",
        };
        return string.IsNullOrWhiteSpace(checkName) ? $"Health check {did}" : $"{checkName}: {did}";
    }

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
    /// Charted devices, keyed by normalized MAC so both sources can be matched against them. The
    /// value carries the series key the chart filters on (the target's own MAC spelling) and the
    /// device's display name, which rides down with each event so the mark layer never has to
    /// look a series up.
    /// </param>
    /// <param name="from">Window start.</param>
    /// <param name="to">Window end.</param>
    /// <param name="ct">Cancellation token.</param>
    private static async Task<List<object>> BuildAnnotationsAsync(
        MonitoringInfluxClient influx,
        NetworkOptimizerDbContext db,
        Dictionary<string, (string Key, string Name)> macsByNormalized,
        DateTime from,
        DateTime to,
        CancellationToken ct)
    {
        // Carried alongside the payload so the two sources can be merged into one time-ordered
        // list; the payload itself stays an anonymous object to keep the JSON property names
        // literal, the way the rest of this endpoint's response is built.
        var events = new List<(DateTime At, object Payload)>();
        if (macsByNormalized.Count == 0) return new List<object>();

        // Reboots. Influx holds these in the long-term bucket, so this reaches as far back as the
        // chart's 30d preset does - but "one record per boot" is the intent, not the reality, so
        // the raw rows are collapsed to one mark per boot first.
        var reboots = CollapseToOneRecordPerBoot(
            await influx.QueryDeviceRebootsInRangeAsync(from, to, ct));

        foreach (var reboot in reboots)
        {
            if (!macsByNormalized.TryGetValue(NormalizeMac(reboot.DeviceMac), out var series)) continue;

            var unexpected = UnexpectedRebootCategories.Contains(reboot.Category);
            events.Add((reboot.BootedAt, new
            {
                key = series.Key,
                device = series.Name,
                time = reboot.BootedAt.ToString("o"),
                kind = "reboot",
                severity = unexpected ? "warning" : "info",
                title = string.IsNullOrWhiteSpace(reboot.Summary) ? "Restarted" : reboot.Summary,
                detail = string.IsNullOrWhiteSpace(reboot.FirmwareVersion)
                    ? reboot.Detail
                    : string.IsNullOrWhiteSpace(reboot.Detail)
                        ? $"Firmware {reboot.FirmwareVersion}"
                        : $"{reboot.Detail}. Firmware {reboot.FirmwareVersion}",
                firmware = FirmwareVersionFormat.ShortOrNull(reboot.FirmwareVersion),
            }));
        }

        // Health check remedies. Long-term like reboots, and drawn as their own kind: the alert
        // that announced the action is filtered out below so one restart is not marked twice.
        foreach (var action in await influx.QueryHealthCheckEventsAsync(from, to, ct))
        {
            if (!macsByNormalized.TryGetValue(NormalizeMac(action.DeviceMac), out var series)) continue;
            events.Add((action.At, new
            {
                key = series.Key,
                device = series.Name,
                time = action.At.ToString("o"),
                kind = "action",
                severity = action.Severity == "critical" ? "critical" : "warning",
                title = ActionLabel(action.CheckName, action.Remedy),
                detail = action.Detail,
                reading = action.Value?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
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
                kvp.Value.Key, kvp.Value.Key.ToLowerInvariant(), kvp.Value.Key.ToUpperInvariant(),
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
            if (!macsByNormalized.TryGetValue(NormalizeMac(alert.DeviceId!), out var series)) continue;

            // TriggeredAt is written as UtcNow but comes back from SQLite as Unspecified, and "o"
            // on an Unspecified value emits no zone - which the browser then reads as LOCAL time,
            // putting every alert mark hours away from the event it describes.
            var triggeredAtUtc = DateTime.SpecifyKind(alert.TriggeredAt, DateTimeKind.Utc);

            events.Add((triggeredAtUtc, new
            {
                key = series.Key,
                device = series.Name,
                time = triggeredAtUtc.ToString("o"),
                kind = "alert",
                severity = alert.Severity switch
                {
                    AlertSeverity.Critical or AlertSeverity.Error => "critical",
                    AlertSeverity.Warning => "warning",
                    _ => "info",
                },
                title = AlertLabel(alert.EventType, alert.Title),
                detail = alert.Message,
                reading = AlertReading(alert.EventType, alert.Message),
            }));
        }

        return events.OrderBy(e => e.At).Select(e => e.Payload).ToList();
    }

    /// <summary>
    /// Two reboot records this far apart describe the same boot. Matches the tolerance
    /// DeviceRebootTracker itself uses to decide whether a device is still on the boot it
    /// last saw, so the chart and the tracker agree on what "one boot" means.
    /// </summary>
    private static readonly TimeSpan BootMatchTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Collapses the stored reboot records to one per boot.
    ///
    /// A boot is supposed to occupy a single point, rewritten in place when it is re-probed. In
    /// practice one boot can hold several: on the NAS site, 77 stored records over 90 days
    /// describe 11 real boots, in bursts spanning under a second, carrying different classifier
    /// versions and sometimes contradicting each other on the category. That is a write-path
    /// defect and it is not fixed here; the read side just has to stop reporting one restart as
    /// nine, which is what the reboot-reason display already gets for free by taking only the
    /// latest record per device.
    ///
    /// The survivor is the one classified by the newest rules, since a bumped classifier means
    /// the older records were re-probed precisely because their verdict was not trusted; ties go
    /// to the latest instant, which is what the tracker's own seed query picks.
    ///
    /// Records naming different firmware are never merged, however close together they sit: a
    /// device cannot swap images without restarting, so those are separate boots and each earns
    /// its own mark. This is what keeps a reflash pair - a downgrade and the roll-forward behind
    /// it, minutes apart - from being drawn as one restart.
    /// </summary>
    internal static List<MonitoringInfluxClient.DeviceRebootPoint> CollapseToOneRecordPerBoot(
        IReadOnlyList<MonitoringInfluxClient.DeviceRebootPoint> records)
    {
        var kept = new List<MonitoringInfluxClient.DeviceRebootPoint>();

        foreach (var group in records.GroupBy(r => NormalizeMac(r.DeviceMac)))
        {
            MonitoringInfluxClient.DeviceRebootPoint? best = null;
            // Measured from the boot the cluster opened with, not from whichever record is
            // currently winning it - otherwise the window slides forward with every swap and a
            // long enough run of records would swallow a genuinely separate restart.
            DateTime clusterStart = default;
            string? clusterFirmware = null;

            foreach (var record in group.OrderBy(r => r.BootedAt))
            {
                if (best is not null && record.BootedAt - clusterStart <= BootMatchTolerance
                    && !RebootReasonParser.NamesADifferentImage(clusterFirmware, record.FirmwareVersion))
                {
                    clusterFirmware ??= record.FirmwareVersion;
                    if (record.ClassifierVersion > best.ClassifierVersion
                        || (record.ClassifierVersion == best.ClassifierVersion
                            && record.BootedAt >= best.BootedAt))
                    {
                        best = record;
                    }
                    continue;
                }

                if (best is not null) kept.Add(best);
                best = record;
                clusterStart = record.BootedAt;
                clusterFirmware = record.FirmwareVersion;
            }

            if (best is not null) kept.Add(best);
        }

        return kept;
    }

    /// <summary>
    /// Short label for an alert mark: the alert's own noun, without the device name the chart
    /// already shows. Falls back to the stored title when the event type is not one we name.
    /// </summary>
    internal static string AlertLabel(string eventType, string title) => eventType switch
    {
        "device.offline" => "Offline",
        "device.recovered" => "Recovered",
        "monitoring.target_offline" => "Device offline",
        "monitoring.target_recovered" => "Device back online",
        DeviceHealthAlertEvaluator.HighCpuEventType => "High CPU",
        DeviceHealthAlertEvaluator.HighMemoryEventType => "High memory",
        DeviceHealthAlertEvaluator.HighTemperatureEventType => "High temperature",
        // The stored title already names the check; the device the chart shows is trimmed off.
        HealthCheckAlertTypes.Failed or HealthCheckAlertTypes.Recovered => StripDeviceSuffix(title),
        _ => title,
    };

    /// <summary>"JVM GC Thrash on Gateway (site x)" becomes "JVM GC Thrash" for the mark.</summary>
    private static string StripDeviceSuffix(string title)
    {
        var site = title.LastIndexOf(" (site ", StringComparison.Ordinal);
        if (site > 0 && title.EndsWith(')')) title = title[..site];
        var on = title.LastIndexOf(" on ", StringComparison.Ordinal);
        return on > 0 ? title[..on] : title;
    }

    private static readonly Regex ReadingPattern = new(@"(\d+\.?\d*)\s*(%|°?C)(?!\w)", RegexOptions.Compiled);

    /// <summary>
    /// Extracts a short metric reading from the alert message for the collapsed chart tooltip
    /// subtitle (e.g. "65.3 °C", "87%"). The regex matches the first number+unit in the message,
    /// so every Message format in <see cref="DeviceHealthAlertEvaluator"/> must keep the reading
    /// as the first such token - and new health alert types must be added to the switch below.
    /// </summary>
    internal static string? AlertReading(string eventType, string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        return eventType switch
        {
            DeviceHealthAlertEvaluator.HighCpuEventType or DeviceHealthAlertEvaluator.HighMemoryEventType or DeviceHealthAlertEvaluator.HighTemperatureEventType
                => ReadingPattern.Match(message) is { Success: true } m
                    ? $"{m.Groups[1].Value}{(m.Groups[2].Value == "%" ? "%" : " °C")}" : null,
            _ => null,
        };
    }

    /// <summary>Strips MAC separators and case so spellings from different sources compare equal.</summary>
    private static string NormalizeMac(string mac) =>
        mac.Replace(":", "").Replace("-", "").ToLowerInvariant();
}
