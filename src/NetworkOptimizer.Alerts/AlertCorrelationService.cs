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

    /// <summary>
    /// Find or create an incident for the given alert event.
    /// Returns the incident if correlated, null if no correlation applies.
    /// </summary>
    public async Task<AlertIncident?> CorrelateAsync(
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
                existingIncident.AlertCount++;
                existingIncident.LastTriggeredAt = DateTime.UtcNow;
                if (alertEvent.Severity > existingIncident.Severity)
                    existingIncident.Severity = alertEvent.Severity;

                await repository.UpdateIncidentAsync(existingIncident, cancellationToken);

                historyEntry.IncidentId = existingIncident.Id;
                _logger.LogDebug("Correlated alert to incident {IncidentId} ({Key})", existingIncident.Id, correlationKey);
                return existingIncident;
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
            return incident;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to correlate alert");
            return null;
        }
    }
}
