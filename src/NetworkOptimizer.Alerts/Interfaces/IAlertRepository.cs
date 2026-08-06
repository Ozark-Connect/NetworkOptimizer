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

    /// <summary>
    /// One page of alert history, newest first, with the total the filters match so the caller can
    /// say how many pages there are. Paged in SQL: the history of a site that has been running a
    /// while is far more than a page, and the flat take showed only its newest slice with nothing
    /// to say the rest existed.
    /// </summary>
    Task<(List<AlertHistoryEntry> Items, int Total)> GetAlertHistoryPageAsync(int skip, int take, string? source = null, AlertSeverity? minSeverity = null, CancellationToken cancellationToken = default);
    Task<AlertHistoryEntry?> GetAlertAsync(int id, CancellationToken cancellationToken = default);
    Task<List<AlertHistoryEntry>> GetAlertsForDigestAsync(DateTime since, CancellationToken cancellationToken = default);
    Task<List<AlertHistoryEntry>> GetUnresolvedAlertsAsync(CancellationToken cancellationToken = default);
    Task<List<AlertHistoryEntry>> GetAlertsByIncidentIdAsync(int incidentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the status of many alerts in ONE round trip, stamping the timestamp that goes with it.
    /// The bulk buttons used to call <see cref="UpdateAlertAsync"/> per alert, and each of those is
    /// a SaveChanges - a SQLite commit - against a change tracker that grew by an entity every
    /// iteration. Returns the number of rows changed.
    /// </summary>
    Task<int> SetAlertStatusAsync(IReadOnlyCollection<int> alertIds, AlertStatus status, DateTime timestamp, CancellationToken cancellationToken = default);

    /// <summary>Incidents by id, in one query - the batch counterpart of GetIncidentAsync.</summary>
    Task<List<AlertIncident>> GetIncidentsByIdsAsync(IReadOnlyCollection<int> incidentIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// The incidents that are not resolved, newest first. Filtered in SQL rather than by the
    /// caller: taking the newest N and filtering afterwards hides an unresolved incident the
    /// moment N newer ones have been resolved, which on a busy site is permanent.
    /// </summary>
    Task<List<AlertIncident>> GetUnresolvedIncidentsAsync(int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every alert belonging to any of these incidents, in one query. Re-deriving a few hundred
    /// incidents one at a time is two round trips each, which is the bulk buttons' remaining cost
    /// once the alerts themselves are written together.
    /// </summary>
    Task<List<AlertHistoryEntry>> GetAlertsByIncidentIdsAsync(IReadOnlyCollection<int> incidentIds, CancellationToken cancellationToken = default);

    /// <summary>Saves several incidents in one round trip.</summary>
    Task UpdateIncidentsAsync(IReadOnlyCollection<AlertIncident> incidents, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks every active alert of the given event types on the given device as resolved and
    /// returns the entries that were closed. Used by the alert pipeline to close open WAN outage
    /// alerts when their recovery - or a superseding total outage - event arrives.
    /// </summary>
    Task<List<AlertHistoryEntry>> ResolveActiveAlertsAsync(IReadOnlyCollection<string> eventTypes, string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks every active alert of the given event types as resolved, whatever device it names,
    /// and returns the entries that were closed. Used when a site-wide alert supersedes the
    /// per-device alerts describing pieces of the same event.
    /// </summary>
    Task<List<AlertHistoryEntry>> ResolveActiveAlertsAnyDeviceAsync(IReadOnlyCollection<string> eventTypes, CancellationToken cancellationToken = default);

    // --- Alert Incidents ---
    Task<int> SaveIncidentAsync(AlertIncident incident, CancellationToken cancellationToken = default);
    Task UpdateIncidentAsync(AlertIncident incident, CancellationToken cancellationToken = default);
    Task<AlertIncident?> GetActiveIncidentByKeyAsync(string correlationKey, CancellationToken cancellationToken = default);
    Task<List<AlertIncident>> GetIncidentsAsync(int limit = 50, CancellationToken cancellationToken = default);
    Task<AlertIncident?> GetIncidentAsync(int id, CancellationToken cancellationToken = default);
}
