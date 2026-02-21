using Microsoft.Extensions.Logging;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Alerts.Models;

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
            _logger.LogDebug(ex, "Failed to correlate alert");
            return null;
        }
    }
}
