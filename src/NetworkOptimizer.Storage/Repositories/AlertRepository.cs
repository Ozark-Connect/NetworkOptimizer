using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Repositories;

/// <summary>
/// Repository for alert rules, delivery channels, history, and incidents.
/// </summary>
public class AlertRepository : IAlertRepository
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly ILogger<AlertRepository> _logger;

    public AlertRepository(NetworkOptimizerDbContext context, ILogger<AlertRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #region Alert Rules

    public async Task<List<AlertRule>> GetRulesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.AlertRules
                .AsNoTracking()
                .OrderBy(r => r.Name)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get alert rules");
            throw;
        }
    }

    public async Task<List<AlertRule>> GetEnabledRulesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.AlertRules
                .AsNoTracking()
                .Where(r => r.IsEnabled)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get enabled alert rules");
            throw;
        }
    }

    public async Task<AlertRule?> GetRuleAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.AlertRules
                .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get alert rule {RuleId}", id);
            throw;
        }
    }

    public async Task<int> SaveRuleAsync(AlertRule rule, CancellationToken cancellationToken = default)
    {
        try
        {
            rule.CreatedAt = DateTime.UtcNow;
            _context.AlertRules.Add(rule);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Saved alert rule {RuleId}: {Name}", rule.Id, rule.Name);
            return rule.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save alert rule {Name}", rule.Name);
            throw;
        }
    }

    public async Task UpdateRuleAsync(AlertRule rule, CancellationToken cancellationToken = default)
    {
        try
        {
            rule.UpdatedAt = DateTime.UtcNow;
            _context.AlertRules.Update(rule);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Updated alert rule {RuleId}: {Name}", rule.Id, rule.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update alert rule {RuleId}", rule.Id);
            throw;
        }
    }

    public async Task DeleteRuleAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            var rule = await _context.AlertRules.FindAsync([id], cancellationToken);
            if (rule != null)
            {
                _context.AlertRules.Remove(rule);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Deleted alert rule {RuleId}: {Name}", id, rule.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete alert rule {RuleId}", id);
            throw;
        }
    }

    #endregion

    #region Delivery Channels

    public async Task<List<DeliveryChannel>> GetChannelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.DeliveryChannels
                .AsNoTracking()
                .OrderBy(c => c.Name)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get delivery channels");
            throw;
        }
    }

    public async Task<List<DeliveryChannel>> GetEnabledChannelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.DeliveryChannels
                .AsNoTracking()
                .Where(c => c.IsEnabled)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get enabled delivery channels");
            throw;
        }
    }

    public async Task<DeliveryChannel?> GetChannelAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.DeliveryChannels
                .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get delivery channel {ChannelId}", id);
            throw;
        }
    }

    public async Task<int> SaveChannelAsync(DeliveryChannel channel, CancellationToken cancellationToken = default)
    {
        try
        {
            channel.CreatedAt = DateTime.UtcNow;
            _context.DeliveryChannels.Add(channel);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Saved delivery channel {ChannelId}: {Name}", channel.Id, channel.Name);
            return channel.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save delivery channel {Name}", channel.Name);
            throw;
        }
    }

    public async Task UpdateChannelAsync(DeliveryChannel channel, CancellationToken cancellationToken = default)
    {
        try
        {
            channel.UpdatedAt = DateTime.UtcNow;
            _context.DeliveryChannels.Update(channel);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Updated delivery channel {ChannelId}: {Name}", channel.Id, channel.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update delivery channel {ChannelId}", channel.Id);
            throw;
        }
    }

    public async Task DeleteChannelAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            var channel = await _context.DeliveryChannels.FindAsync([id], cancellationToken);
            if (channel != null)
            {
                _context.DeliveryChannels.Remove(channel);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Deleted delivery channel {ChannelId}: {Name}", id, channel.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete delivery channel {ChannelId}", id);
            throw;
        }
    }

    #endregion

    #region Alert History

    public async Task<int> SaveAlertAsync(AlertHistoryEntry alert, CancellationToken cancellationToken = default)
    {
        try
        {
            _context.AlertHistory.Add(alert);
            await _context.SaveChangesAsync(cancellationToken);
            return alert.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save alert history entry");
            throw;
        }
    }

    public async Task UpdateAlertAsync(AlertHistoryEntry alert, CancellationToken cancellationToken = default)
    {
        try
        {
            _context.AlertHistory.Update(alert);
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update alert history entry {AlertId}", alert.Id);
            throw;
        }
    }

    public async Task<List<AlertHistoryEntry>> GetActiveAlertsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.AlertHistory
                .AsNoTracking()
                .Where(a => a.Status == AlertStatus.Active)
                .OrderByDescending(a => a.TriggeredAt)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get active alerts");
            throw;
        }
    }

    public async Task<List<AlertHistoryEntry>> GetAlertHistoryAsync(
        int limit = 100,
        string? source = null,
        AlertSeverity? minSeverity = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = _context.AlertHistory.AsNoTracking().AsQueryable();

            if (!string.IsNullOrEmpty(source))
                query = query.Where(a => a.Source == source);

            if (minSeverity.HasValue)
                query = query.Where(a => a.Severity >= minSeverity.Value);

            return await query
                .OrderByDescending(a => a.TriggeredAt)
                .Take(limit)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get alert history");
            throw;
        }
    }

    public async Task<AlertHistoryEntry?> GetAlertAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.AlertHistory
                .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get alert {AlertId}", id);
            throw;
        }
    }

    public async Task<List<AlertHistoryEntry>> GetAlertsForDigestAsync(DateTime since, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.AlertHistory
                .AsNoTracking()
                .Where(a => a.TriggeredAt >= since)
                .OrderByDescending(a => a.TriggeredAt)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get alerts for digest");
            throw;
        }
    }

    public async Task<List<AlertHistoryEntry>> GetUnresolvedAlertsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.AlertHistory
                .AsNoTracking()
                .Where(a => a.Status != AlertStatus.Resolved)
                .OrderByDescending(a => a.TriggeredAt)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get unresolved alerts");
            throw;
        }
    }

    public async Task<List<AlertHistoryEntry>> GetAlertsByIncidentIdAsync(int incidentId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.AlertHistory
                .AsNoTracking()
                .Where(a => a.IncidentId == incidentId)
                .OrderByDescending(a => a.TriggeredAt)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get alerts for incident {IncidentId}", incidentId);
            throw;
        }
    }

    #endregion

    #region Alert Incidents

    public async Task<int> SaveIncidentAsync(AlertIncident incident, CancellationToken cancellationToken = default)
    {
        try
        {
            _context.AlertIncidents.Add(incident);
            await _context.SaveChangesAsync(cancellationToken);
            return incident.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save alert incident");
            throw;
        }
    }

    public async Task UpdateIncidentAsync(AlertIncident incident, CancellationToken cancellationToken = default)
    {
        try
        {
            _context.AlertIncidents.Update(incident);
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update alert incident {IncidentId}", incident.Id);
            throw;
        }
    }

    public async Task<AlertIncident?> GetActiveIncidentByKeyAsync(string correlationKey, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.AlertIncidents
                .Where(i => i.CorrelationKey == correlationKey && i.Status == AlertStatus.Active)
                .OrderByDescending(i => i.LastTriggeredAt)
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get active incident for key {Key}", correlationKey);
            throw;
        }
    }

    public async Task<List<AlertIncident>> GetIncidentsAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.AlertIncidents
                .AsNoTracking()
                .OrderByDescending(i => i.LastTriggeredAt)
                .Take(limit)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get alert incidents");
            throw;
        }
    }

    public async Task<AlertIncident?> GetIncidentAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.AlertIncidents
                .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get alert incident {IncidentId}", id);
            throw;
        }
    }

    #endregion
}
