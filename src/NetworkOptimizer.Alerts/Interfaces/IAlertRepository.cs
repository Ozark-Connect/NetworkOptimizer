using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Alerts.Interfaces;

/// <summary>
/// Repository for alert rules, delivery channels, history, and incidents.
/// </summary>
public interface IAlertRepository
{
    // --- Alert Rules ---
    Task<List<AlertRule>> GetRulesAsync(CancellationToken cancellationToken = default);
    Task<List<AlertRule>> GetEnabledRulesAsync(CancellationToken cancellationToken = default);
    Task<AlertRule?> GetRuleAsync(int id, CancellationToken cancellationToken = default);
    Task<int> SaveRuleAsync(AlertRule rule, CancellationToken cancellationToken = default);
    Task UpdateRuleAsync(AlertRule rule, CancellationToken cancellationToken = default);
    Task DeleteRuleAsync(int id, CancellationToken cancellationToken = default);

    // --- Delivery Channels ---
    Task<List<DeliveryChannel>> GetChannelsAsync(CancellationToken cancellationToken = default);
    Task<List<DeliveryChannel>> GetEnabledChannelsAsync(CancellationToken cancellationToken = default);
    Task<DeliveryChannel?> GetChannelAsync(int id, CancellationToken cancellationToken = default);
    Task<int> SaveChannelAsync(DeliveryChannel channel, CancellationToken cancellationToken = default);
    Task UpdateChannelAsync(DeliveryChannel channel, CancellationToken cancellationToken = default);
    Task DeleteChannelAsync(int id, CancellationToken cancellationToken = default);

    // --- Alert History ---
    Task<int> SaveAlertAsync(AlertHistoryEntry alert, CancellationToken cancellationToken = default);
    Task UpdateAlertAsync(AlertHistoryEntry alert, CancellationToken cancellationToken = default);
    Task<List<AlertHistoryEntry>> GetActiveAlertsAsync(CancellationToken cancellationToken = default);
    Task<List<AlertHistoryEntry>> GetAlertHistoryAsync(int limit = 100, string? source = null, AlertSeverity? minSeverity = null, CancellationToken cancellationToken = default);
    Task<AlertHistoryEntry?> GetAlertAsync(int id, CancellationToken cancellationToken = default);
    Task<List<AlertHistoryEntry>> GetAlertsForDigestAsync(DateTime since, CancellationToken cancellationToken = default);

    // --- Alert Incidents ---
    Task<int> SaveIncidentAsync(AlertIncident incident, CancellationToken cancellationToken = default);
    Task UpdateIncidentAsync(AlertIncident incident, CancellationToken cancellationToken = default);
    Task<AlertIncident?> GetActiveIncidentByKeyAsync(string correlationKey, CancellationToken cancellationToken = default);
    Task<List<AlertIncident>> GetIncidentsAsync(int limit = 50, CancellationToken cancellationToken = default);
    Task<AlertIncident?> GetIncidentAsync(int id, CancellationToken cancellationToken = default);
}
