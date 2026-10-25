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

/// <summary>An alert incident and how it relates to config changes.</summary>
public sealed record ScopedIncident(AlertIncident Incident, IncidentRelevance Relevance)
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

    /// <summary>Every alert type that can place an incident next to a change, for the repository's filter.</summary>
    public static readonly IReadOnlyCollection<string> EventTypes = [.. LinkedTypes, .. WanTypes];

    /// <summary>One alert's relevance.</summary>
    public static IncidentRelevance Classify(AlertHistoryEntry alert)
    {
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
            .Select(x => new ScopedIncident(x.Incident, Classify(x.Alerts)))
            .Where(x => x.Relevance != IncidentRelevance.Unrelated)
            .ToList();

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
