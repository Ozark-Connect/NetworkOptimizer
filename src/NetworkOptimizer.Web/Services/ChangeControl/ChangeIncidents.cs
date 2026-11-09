using System.Text.Json;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring.RebootReason;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>How an alert incident relates to config changes (spec "Changes and incidents").</summary>
public enum IncidentRelevance
{
    /// <summary>Nothing an operator, Network Optimizer, or UniFi Network changes causes it. Not shown.</summary>
    Unrelated,

    /// <summary>A WAN or ISP incident: listed after a change, never linked to it.</summary>
    Wan,

    /// <summary>A LAN or device incident a config change can cause: linked and counted.</summary>
    Linked,
}

/// <summary>An alert incident, how it relates to config changes, and when its condition ended.</summary>
public sealed record ScopedIncident(AlertIncident Incident, IncidentRelevance Relevance, DateTime End)
{
    public bool IsWan => Relevance == IncidentRelevance.Wan;
}

/// <summary>
/// Decides which alert incidents Change Control places next to config changes. An allowlist:
/// provider equipment (Starlink, cable modem, ONT, cellular, SFP optics), internet path targets,
/// threats, audits, Firmware Rollout notices, scheduled tasks, Network Optimizer's own connectivity
/// (agent, console), recoveries, and alert types added later stay out until listed here.
/// </summary>
public static class ChangeIncidents
{
    private static readonly HashSet<string> LinkedTypes =
    [
        "device.offline", "device.rebooted", "device.gateway_high_cpu", "device.gateway_high_memory",
        "monitoring.target_offline", "monitoring.target_sustained_loss",
        "monitoring.health_check_failed", "monitoring.health_check_action",
        "monitoring.radio_wedged", "monitoring.radio_resets", "wifi.congestion",
        "speedtest.regression", "speedtest.client_regression",
    ];

    private static readonly HashSet<string> WanTypes =
    [
        "monitoring.wan_outage", "monitoring.wan_outage_partial", "wan.speed_degradation", "wan.link_speed_degraded",
    ];

    /// <summary>The event that ends each alert with one. A recovery does not resolve its alert: people do, later.</summary>
    internal static readonly IReadOnlyDictionary<string, string> RecoveryFor = new Dictionary<string, string>
    {
        ["device.offline"] = "device.recovered",
        ["monitoring.target_offline"] = "monitoring.target_recovered",
        ["monitoring.wan_outage"] = "monitoring.wan_recovered",
        ["monitoring.wan_outage_partial"] = "monitoring.wan_recovered",
        ["monitoring.health_check_failed"] = "monitoring.health_check_recovered",
    };

    /// <summary>
    /// Every alert type that can place an incident next to a change, plus the recoveries that end them,
    /// for the repository's filter. A recovery alone classifies as unrelated, so it places nothing.
    /// </summary>
    public static readonly IReadOnlyCollection<string> EventTypes = [.. LinkedTypes, .. WanTypes, .. RecoveryFor.Values.Distinct()];

    /// <summary>
    /// When an alert's condition ended: its first matching recovery after it fired (same device, WAN, or
    /// target), else its resolution, else null while it lasts.
    /// </summary>
    public static DateTime? ConditionEnd(AlertHistoryEntry alert, IEnumerable<AlertHistoryEntry> others)
    {
        if (RecoveryFor.TryGetValue(alert.EventType, out var recovery))
        {
            var subject = Subject(alert);
            var recovered = others
                .Where(o => o.EventType == recovery && o.TriggeredAt >= alert.TriggeredAt && Subject(o) == subject)
                .OrderBy(o => o.TriggeredAt).FirstOrDefault();
            if (recovered != null)
                return alert.ResolvedAt is { } r && r < recovered.TriggeredAt ? r : recovered.TriggeredAt;
        }
        return alert.ResolvedAt;
    }

    // Device MACs arrive with and without colons; WAN alerts carry the wan key.
    private static string Subject(AlertHistoryEntry a) =>
        (a.DeviceId ?? Context(a, "wan") ?? a.DeviceIp ?? "").Replace(":", "").Replace("-", "").ToLowerInvariant();

    /// <summary>One alert's relevance.</summary>
    public static IncidentRelevance Classify(AlertHistoryEntry alert)
    {
        // Starlink's throughput swings with the sky and its own schedule; a speed drop there says nothing about a change.
        if (alert.EventType == "wan.speed_degradation" && StarlinkWanDetector.IsStarlinkWan(Context(alert, "wan_name")))
            return IncidentRelevance.Unrelated;
        if (WanTypes.Contains(alert.EventType)) return IncidentRelevance.Wan;
        if (!LinkedTypes.Contains(alert.EventType)) return IncidentRelevance.Unrelated;
        return alert.EventType switch
        {
            "monitoring.target_offline" or "monitoring.target_sustained_loss" => IsLanTarget(alert) ? IncidentRelevance.Linked : IncidentRelevance.Unrelated,
            "device.rebooted" => IsChangeableReboot(alert) ? IncidentRelevance.Linked : IncidentRelevance.Unrelated,
            _ => IncidentRelevance.Linked,
        };
    }

    /// <summary>An incident takes the strongest relevance among its alerts.</summary>
    public static IncidentRelevance Classify(IEnumerable<AlertHistoryEntry> alerts) =>
        alerts.Select(Classify).DefaultIfEmpty(IncidentRelevance.Unrelated).Max();

    /// <summary>Incidents that started in a range, unrelated ones left out.</summary>
    public static async Task<List<ScopedIncident>> StartedAsync(IChangeControlRepository repo, DateTime fromUtc, DateTime toUtc, CancellationToken ct) =>
        (await repo.GetIncidentsStartedAsync(fromUtc, toUtc, EventTypes, ct))
            .Select(x => new ScopedIncident(x.Incident, Classify(x.Alerts), End(x.Incident, x.Alerts, DateTime.UtcNow)))
            .Where(x => x.Relevance != IncidentRelevance.Unrelated)
            .ToList();

    // Alerts a recovery resolves on its own: the incident's resolution is when the condition ended.
    private static readonly HashSet<string> RecoveringTypes =
    [
        "device.offline", "monitoring.target_offline", "monitoring.target_sustained_loss",
        "monitoring.wan_outage", "monitoring.wan_outage_partial", "monitoring.health_check_failed",
    ];

    /// <summary>How long a one-shot alert's condition is drawn past its last firing.</summary>
    internal static readonly TimeSpan OneShotSpan = TimeSpan.FromMinutes(15);

    /// <summary>
    /// When the incident's condition ended, never when a person resolved it. Alerts with a recovery end
    /// at it (or now, while none has come); every other alert fires once and ends shortly after its last
    /// firing, or earlier if resolved sooner.
    /// </summary>
    internal static DateTime End(AlertIncident incident, IReadOnlyCollection<AlertHistoryEntry> alerts, DateTime nowUtc)
    {
        var oneShotEnd = incident.LastTriggeredAt + OneShotSpan;
        var recovering = alerts.Where(a => RecoveringTypes.Contains(a.EventType)).ToList();
        if (recovering.Count == 0)
            return incident.ResolvedAt is { } r && r < oneShotEnd ? r : Min(oneShotEnd, nowUtc);
        // An alert with neither a recovery nor a resolution of its own ends with the incident.
        var ends = recovering.Select(a => ConditionEnd(a, alerts) ?? incident.ResolvedAt).ToList();
        return ends.Any(e => e == null) ? nowUtc : Max(ends.Max()!.Value, incident.LastTriggeredAt);
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    /// <summary>
    /// Fabric targets are the site's own UniFi devices; a Custom target counts when it is on the LAN.
    /// Access ISP, Transit, Internet Service, and WAN targets roll up into the WAN outage alerts instead.
    /// </summary>
    private static bool IsLanTarget(AlertHistoryEntry alert) => Context(alert, "target_type") switch
    {
        nameof(MonitoringTargetType.Fabric) => true,
        nameof(MonitoringTargetType.Custom) => alert.DeviceIp is { } ip && NetworkUtilities.IsPrivateIpAddress(ip),
        _ => false,
    };

    /// <summary>
    /// Power loss, a power cycle, and a firmware upgrade are not config changes. Alerts raised before
    /// reboot_category was recorded fall back to the reason summary at the start of the message.
    /// </summary>
    private static bool IsChangeableReboot(AlertHistoryEntry alert)
    {
        if (Enum.TryParse<RebootCategory>(Context(alert, "reboot_category"), out var category))
            return category is not (RebootCategory.PowerLoss or RebootCategory.PowerCycle or RebootCategory.FirmwareUpgrade or RebootCategory.Unknown);
        return !(alert.Message.Contains("restarted: Power", StringComparison.Ordinal)
            || alert.Message.Contains("restarted: Firmware upgrade", StringComparison.Ordinal));
    }

    private static string? Context(AlertHistoryEntry alert, string key)
    {
        if (string.IsNullOrEmpty(alert.ContextJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(alert.ContextJson);
            return doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
