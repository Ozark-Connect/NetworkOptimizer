using NetworkOptimizer.Alerts;
using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The gated write path for alert configuration and alert state: rules, delivery channels, schedule
/// tasks, and acknowledging/resolving alerts. The endpoints and the Alerts page go through here so
/// every change is Admin-gated and audited (design doc 06, gate 9; <c>alert_rule.changed</c> from the
/// doc-05 coverage list). Reads stay on <see cref="IAlertRepository"/>.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IAlertConfigService
{
    /// <summary>Creates an alert rule and returns its id.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.AlertRuleChanged, TargetType = "alert_rule")]
    Task<int> CreateRuleAsync(AlertRule rule);

    /// <summary>Applies edits to an existing rule; returns the saved rule, or null when it is gone.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.AlertRuleChanged, TargetType = "alert_rule")]
    Task<AlertRule?> UpdateRuleAsync(int id, AlertRule rule);

    /// <summary>Deletes an alert rule.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.AlertRuleChanged, TargetType = "alert_rule")]
    Task DeleteRuleAsync(int id);

    /// <summary>Creates a delivery channel and returns its id.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.AlertRuleChanged, TargetType = "alert_channel")]
    Task<int> CreateChannelAsync(DeliveryChannel channel);

    /// <summary>Applies edits to a delivery channel; returns the saved channel, or null when it is gone.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.AlertRuleChanged, TargetType = "alert_channel")]
    Task<DeliveryChannel?> UpdateChannelAsync(int id, DeliveryChannel channel);

    /// <summary>Deletes a delivery channel.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.AlertRuleChanged, TargetType = "alert_channel")]
    Task DeleteChannelAsync(int id);

    // Dealing with an alert is operating the network, not configuring it: it happens constantly, and
    // getting it wrong is fixed by doing it again. Deciding WHAT alerts, and where they are sent, is
    // configuration and stays above.

    /// <summary>Acknowledges an alert; returns the updated alert, or null when it is gone.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.AlertRuleChanged, TargetType = "alert")]
    Task<AlertHistoryEntry?> AcknowledgeAlertAsync(int id);

    /// <summary>Resolves an alert; returns the updated alert, or null when it is gone.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.AlertRuleChanged, TargetType = "alert")]
    Task<AlertHistoryEntry?> ResolveAlertAsync(int id);

    /// <summary>Saves an alert's state - acknowledged, resolved, dismissed.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.AlertRuleChanged, TargetType = "alert")]
    Task UpdateAlertAsync(AlertHistoryEntry alert);

    /// <summary>
    /// Sets many alerts to one status in a single round trip. What the bulk buttons use: updating
    /// them one at a time is a database commit each, and slows to a crawl on a few hundred alerts.
    /// </summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.AlertRuleChanged, TargetType = "alert")]
    Task<int> SetAlertStatusAsync(IReadOnlyCollection<int> alertIds, AlertStatus status, DateTime timestamp);

    /// <summary>Saves an incident's state, which the alert list edits alongside its alerts.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.AlertRuleChanged, TargetType = "incident")]
    Task UpdateIncidentAsync(AlertIncident incident);

    /// <summary>
    /// Saves several incidents in one round trip. What the bulk incident buttons use: saving them
    /// one at a time is a database commit each.
    /// </summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.AlertRuleChanged, TargetType = "incident")]
    Task UpdateIncidentsAsync(IReadOnlyCollection<AlertIncident> incidents);

    /// <summary>Runs a scheduled task immediately. Returns false when it could not be started.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.ScheduleChanged, TargetType = "schedule")]
    Task<bool> RunScheduleNowAsync(int id, [SiteSlug] string siteSlug);

    /// <summary>Creates a scheduled task and returns its id.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ScheduleChanged, TargetType = "schedule")]
    Task<int> CreateScheduleAsync(ScheduledTask task);

    /// <summary>Applies edits to a scheduled task; returns the saved task, or null when it is gone.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ScheduleChanged, TargetType = "schedule")]
    Task<ScheduledTask?> UpdateScheduleAsync(int id, ScheduledTask updated);

    /// <summary>Deletes a scheduled task.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ScheduleChanged, TargetType = "schedule")]
    Task DeleteScheduleAsync(int id);
}

/// <inheritdoc />
public sealed class AlertConfigService : IAlertConfigService
{
    private readonly IAlertRepository _alerts;
    private readonly IScheduleRepository _schedules;
    private readonly IAuditContext _auditContext;

    private readonly ScheduleService _scheduleService;

    public AlertConfigService(
        IAlertRepository alerts,
        IScheduleRepository schedules,
        ScheduleService scheduleService,
        IAuditContext auditContext)
    {
        _alerts = alerts;
        _schedules = schedules;
        _scheduleService = scheduleService;
        _auditContext = auditContext;
    }

    /// <inheritdoc />
    public async Task UpdateAlertAsync(AlertHistoryEntry alert)
    {
        await _alerts.UpdateAlertAsync(alert);
        _auditContext.SetTarget(alert.Id.ToString(), alert.EventType);
        _auditContext.SetDetails(new { alert.AcknowledgedAt, alert.ResolvedAt });
    }

    /// <inheritdoc />
    public async Task<int> SetAlertStatusAsync(IReadOnlyCollection<int> alertIds, AlertStatus status, DateTime timestamp)
    {
        var changed = await _alerts.SetAlertStatusAsync(alertIds, status, timestamp);
        // One audit entry for the action, not one per alert: the bulk buttons are a single
        // deliberate act and the count is what makes it readable afterwards.
        _auditContext.SetTarget($"{changed} alert(s)", status.ToString());
        _auditContext.SetDetails(new { Status = status.ToString(), Count = changed, Timestamp = timestamp });
        return changed;
    }

    /// <inheritdoc />
    public async Task UpdateIncidentAsync(AlertIncident incident)
    {
        await _alerts.UpdateIncidentAsync(incident);
        _auditContext.SetTarget(incident.Id.ToString(), incident.Title);
    }

    /// <inheritdoc />
    public async Task UpdateIncidentsAsync(IReadOnlyCollection<AlertIncident> incidents)
    {
        await _alerts.UpdateIncidentsAsync(incidents);
        _auditContext.SetTarget($"{incidents.Count} incident(s)", "bulk");
    }

    /// <inheritdoc />
    public async Task<bool> RunScheduleNowAsync(int id, string siteSlug)
    {
        var started = await _scheduleService.RunNowAsync(id, siteSlug);
        _auditContext.SetTarget(id.ToString());
        _auditContext.SetDetails(new { ranNow = true, started });
        return started;
    }

    /// <inheritdoc />
    public async Task<int> CreateScheduleAsync(ScheduledTask task)
    {
        var id = await _schedules.SaveAsync(task);
        _auditContext.SetTarget(id.ToString(), task.Name);
        _auditContext.SetDetails(new { created = true, task.TaskType, task.Enabled });
        return id;
    }

    /// <inheritdoc />
    public async Task DeleteScheduleAsync(int id)
    {
        await _schedules.DeleteAsync(id);
        _auditContext.SetTarget(id.ToString());
        _auditContext.SetDetails(new { deleted = true });
    }

    /// <inheritdoc />
    public async Task<int> CreateRuleAsync(AlertRule rule)
    {
        var id = await _alerts.SaveRuleAsync(rule);
        _auditContext.SetTarget(id.ToString(), rule.Name);
        _auditContext.SetDetails(new { created = true, rule.EventTypePattern, rule.IsEnabled });
        return id;
    }

    /// <inheritdoc />
    public async Task<AlertRule?> UpdateRuleAsync(int id, AlertRule rule)
    {
        var existing = await _alerts.GetRuleAsync(id);
        if (existing == null)
            return null;

        existing.Name = rule.Name;
        existing.IsEnabled = rule.IsEnabled;
        existing.EventTypePattern = rule.EventTypePattern;
        existing.Source = rule.Source;
        existing.MinSeverity = rule.MinSeverity;
        existing.CooldownSeconds = rule.CooldownSeconds;
        existing.EscalationMinutes = rule.EscalationMinutes;
        existing.EscalationSeverity = rule.EscalationSeverity;
        existing.DigestOnly = rule.DigestOnly;
        existing.TargetDevices = rule.TargetDevices;
        existing.ThresholdPercent = rule.ThresholdPercent;

        await _alerts.UpdateRuleAsync(existing);
        _auditContext.SetTarget(id.ToString(), existing.Name);
        _auditContext.SetDetails(new { existing.IsEnabled, existing.EventTypePattern, existing.MinSeverity });
        return existing;
    }

    /// <inheritdoc />
    public async Task DeleteRuleAsync(int id)
    {
        await _alerts.DeleteRuleAsync(id);
        _auditContext.SetTarget(id.ToString());
        _auditContext.SetDetails(new { deleted = true });
    }

    /// <inheritdoc />
    public async Task<int> CreateChannelAsync(DeliveryChannel channel)
    {
        var id = await _alerts.SaveChannelAsync(channel);
        _auditContext.SetTarget(id.ToString(), channel.Name);
        // ConfigJson carries channel secrets (tokens, SMTP passwords) - never audited.
        _auditContext.SetDetails(new { created = true, channel.ChannelType, channel.IsEnabled });
        return id;
    }

    /// <inheritdoc />
    public async Task<DeliveryChannel?> UpdateChannelAsync(int id, DeliveryChannel channel)
    {
        var existing = await _alerts.GetChannelAsync(id);
        if (existing == null)
            return null;

        var configChanged = existing.ConfigJson != channel.ConfigJson;

        existing.Name = channel.Name;
        existing.IsEnabled = channel.IsEnabled;
        existing.ChannelType = channel.ChannelType;
        existing.ConfigJson = channel.ConfigJson;
        existing.MinSeverity = channel.MinSeverity;
        existing.DigestEnabled = channel.DigestEnabled;
        existing.DigestSchedule = channel.DigestSchedule;

        await _alerts.UpdateChannelAsync(existing);
        _auditContext.SetTarget(id.ToString(), existing.Name);
        _auditContext.SetDetails(new
        {
            existing.ChannelType,
            existing.IsEnabled,
            existing.MinSeverity,
            config = configChanged ? "***changed***" : "unchanged",
        });
        return existing;
    }

    /// <inheritdoc />
    public async Task DeleteChannelAsync(int id)
    {
        await _alerts.DeleteChannelAsync(id);
        _auditContext.SetTarget(id.ToString());
        _auditContext.SetDetails(new { deleted = true });
    }

    /// <inheritdoc />
    public Task<AlertHistoryEntry?> AcknowledgeAlertAsync(int id)
        => SetAlertStatusAsync(id, AlertStatus.Acknowledged);

    /// <inheritdoc />
    public Task<AlertHistoryEntry?> ResolveAlertAsync(int id)
        => SetAlertStatusAsync(id, AlertStatus.Resolved);

    /// <inheritdoc />
    public async Task<ScheduledTask?> UpdateScheduleAsync(int id, ScheduledTask updated)
    {
        var existing = await _schedules.GetByIdAsync(id);
        if (existing == null)
            return null;

        existing.Enabled = updated.Enabled;
        existing.FrequencyMinutes = updated.FrequencyMinutes;
        existing.Name = updated.Name;

        // Recalculate next run using CalculateNextRun to avoid drift from execution duration
        existing.NextRunAt = ScheduleService.CalculateNextRun(
            existing.FrequencyMinutes, existing.CustomMorningHour, existing.CustomMorningMinute,
            existing.NextRunAt);

        await _schedules.UpdateAsync(existing);
        _auditContext.SetTarget(id.ToString(), existing.Name);
        _auditContext.SetDetails(new { existing.Enabled, existing.FrequencyMinutes });
        return existing;
    }

    private async Task<AlertHistoryEntry?> SetAlertStatusAsync(int id, AlertStatus status)
    {
        var alert = await _alerts.GetAlertAsync(id);
        if (alert == null)
            return null;

        alert.Status = status;
        if (status == AlertStatus.Acknowledged)
            alert.AcknowledgedAt = DateTime.UtcNow;
        else
            alert.ResolvedAt = DateTime.UtcNow;

        await _alerts.UpdateAlertAsync(alert);
        await RecalculateIncidentStatusAsync(alert);

        _auditContext.SetTarget(id.ToString(), alert.Title);
        _auditContext.SetDetails(new { status = status.ToString() });
        return alert;
    }

    private Task RecalculateIncidentStatusAsync(AlertHistoryEntry alert)
        => AlertCorrelationService.RecalculateIncidentStatusAsync(alert, _alerts);
}
