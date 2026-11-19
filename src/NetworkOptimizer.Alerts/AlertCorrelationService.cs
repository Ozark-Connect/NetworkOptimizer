using Microsoft.Extensions.Logging;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Alerts;

/// <summary>
/// Groups related alerts into incidents using correlation keys.
/// </summary>
public class AlertCorrelationService
{
    private readonly ILogger<AlertCorrelationService> _logger;
    private static readonly TimeSpan CorrelationWindow = TimeSpan.FromMinutes(30);

    public AlertCorrelationService(ILogger<AlertCorrelationService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Derive incident status from the statuses of its constituent alerts.
    /// </summary>
    public static (AlertStatus Status, DateTime? ResolvedAt) DeriveIncidentStatus(List<AlertHistoryEntry> alerts)
    {
        if (alerts.Count == 0)
            return (AlertStatus.Active, null);

        if (alerts.All(a => a.Status == AlertStatus.Resolved))
            return (AlertStatus.Resolved, DateTime.UtcNow);

        if (alerts.All(a => a.Status is AlertStatus.Acknowledged or AlertStatus.Resolved))
            return (AlertStatus.Acknowledged, null);

        return (AlertStatus.Active, null);
    }

    /// <summary>
    /// Re-derives the status of the incident an alert belongs to from the statuses of every alert
    /// in that incident, and persists it when it changed. No-op for an uncorrelated alert. Shared
    /// by the UI's acknowledge/resolve actions and by the pipeline's automatic resolution.
    /// </summary>
    /// <summary>
    /// Re-derives many incidents at once: one read for the incidents, one for every alert on them,
    /// and one save. Doing it per incident costs two round trips and a commit each, which is what
    /// the bulk buttons still paid after the alerts themselves were batched - a few hundred alerts
    /// usually means nearly as many incidents, since most incidents hold one alert.
    /// </summary>
    public static async Task RecalculateIncidentStatusesAsync(
        IReadOnlyCollection<int> incidentIds,
        IAlertRepository repository,
        CancellationToken cancellationToken = default)
    {
        if (incidentIds.Count == 0) return;

        var incidents = await repository.GetIncidentsByIdsAsync(incidentIds, cancellationToken);
        if (incidents.Count == 0) return;

        var alertsByIncident = (await repository.GetAlertsByIncidentIdsAsync(incidentIds, cancellationToken))
            .GroupBy(a => a.IncidentId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var changed = new List<AlertIncident>();
        foreach (var incident in incidents)
        {
            if (!alertsByIncident.TryGetValue(incident.Id, out var alerts)) continue;
            var (status, resolvedAt) = DeriveIncidentStatus(alerts);
            if (status == incident.Status) continue;
            incident.Status = status;
            incident.ResolvedAt = resolvedAt;
            changed.Add(incident);
        }

        await repository.UpdateIncidentsAsync(changed, cancellationToken);
    }

    public static async Task RecalculateIncidentStatusAsync(
        AlertHistoryEntry alert,
        IAlertRepository repository,
        CancellationToken cancellationToken = default)
    {
        if (!alert.IncidentId.HasValue) return;

        var incident = await repository.GetIncidentAsync(alert.IncidentId.Value, cancellationToken);
        if (incident == null) return;

        var incidentAlerts = await repository.GetAlertsByIncidentIdAsync(incident.Id, cancellationToken);
        var (newStatus, resolvedAt) = DeriveIncidentStatus(incidentAlerts);

        if (newStatus == incident.Status) return;

        incident.Status = newStatus;
        incident.ResolvedAt = resolvedAt;
        await repository.UpdateIncidentAsync(incident, cancellationToken);
    }

    /// <summary>
    /// Derive a correlation key from an alert event.
    /// Events with the same key within the correlation window will be grouped.
    /// </summary>
    public string? GetCorrelationKey(AlertEvent alertEvent)
    {
        if (!string.IsNullOrEmpty(alertEvent.CorrelationKey))
            return alertEvent.CorrelationKey;

        // Device-level correlation: group by device IP
        if (!string.IsNullOrEmpty(alertEvent.DeviceIp))
            return $"device:{alertEvent.DeviceIp}";

        // Source-level correlation: group by event source + type prefix
        var dotIndex = alertEvent.EventType.IndexOf('.');
        if (dotIndex > 0)
        {
            var prefix = alertEvent.EventType[..dotIndex];
            return $"source:{prefix}";
        }

        return null;
    }

    /// <summary>An alert's incident, whether it joined one already open, and that incident's severity before it joined.</summary>
    public sealed record CorrelationOutcome(AlertIncident Incident, bool Joined, AlertSeverity SeverityBefore);

    /// <summary>
    /// Whether a correlated alert is delivered. Only an explicitly keyed incident holds back an alert, and only one
    /// that joins it at no higher severity without <see cref="AlertEvent.AlwaysDeliver"/>; device and source
    /// incidents deliver every alert, as before.
    /// </summary>
    public static bool ShouldDeliver(AlertEvent alertEvent, CorrelationOutcome? outcome) =>
        outcome is not { Joined: true }
        || string.IsNullOrEmpty(alertEvent.CorrelationKey)
        || alertEvent.AlwaysDeliver
        || alertEvent.Severity > outcome.SeverityBefore;

    /// <summary>
    /// Find or create an incident for the given alert event.
    /// Returns the incident if correlated, null if no correlation applies.
    /// </summary>
    public async Task<AlertIncident?> CorrelateAsync(
        AlertEvent alertEvent,
        AlertHistoryEntry historyEntry,
        IAlertRepository repository,
        CancellationToken cancellationToken = default) =>
        (await CorrelateWithOutcomeAsync(alertEvent, historyEntry, repository, cancellationToken))?.Incident;

    /// <summary>
    /// <see cref="CorrelateAsync"/>, also saying whether the alert joined an open incident and that incident's severity
    /// before it did. Null when no correlation applies.
    /// </summary>
    public async Task<CorrelationOutcome?> CorrelateWithOutcomeAsync(
        AlertEvent alertEvent,
        AlertHistoryEntry historyEntry,
        IAlertRepository repository,
        CancellationToken cancellationToken = default)
    {
        var correlationKey = GetCorrelationKey(alertEvent);
        if (correlationKey == null)
            return null;

        try
        {
            // Look for existing active incident with the same key within the window
            var existingIncident = await repository.GetActiveIncidentByKeyAsync(correlationKey, cancellationToken);

            if (existingIncident != null &&
                (DateTime.UtcNow - existingIncident.LastTriggeredAt) < CorrelationWindow)
            {
                // Add to existing incident
                var severityBefore = existingIncident.Severity;
                existingIncident.AlertCount++;
                existingIncident.LastTriggeredAt = DateTime.UtcNow;
                if (alertEvent.Severity > existingIncident.Severity)
                    existingIncident.Severity = alertEvent.Severity;

                await repository.UpdateIncidentAsync(existingIncident, cancellationToken);

                historyEntry.IncidentId = existingIncident.Id;
                _logger.LogDebug("Correlated alert to incident {IncidentId} ({Key})", existingIncident.Id, correlationKey);
                return new CorrelationOutcome(existingIncident, Joined: true, severityBefore);
            }

            // Create new incident
            var incident = new AlertIncident
            {
                Title = alertEvent.Title,
                Severity = alertEvent.Severity,
                AlertCount = 1,
                CorrelationKey = correlationKey,
                FirstTriggeredAt = DateTime.UtcNow,
                LastTriggeredAt = DateTime.UtcNow
            };

            await repository.SaveIncidentAsync(incident, cancellationToken);

            historyEntry.IncidentId = incident.Id;
            _logger.LogDebug("Created new incident {IncidentId} ({Key})", incident.Id, correlationKey);
            return new CorrelationOutcome(incident, Joined: false, incident.Severity);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to correlate alert");
            return null;
        }
    }
}
