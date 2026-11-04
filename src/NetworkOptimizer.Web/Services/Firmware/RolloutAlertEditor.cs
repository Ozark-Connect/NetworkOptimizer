using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// Corrects a site's open rollout alerts in place, as when a waiting rollout's start moves. An edit
/// is not a new alert: it reaches no Notification Channel.
/// </summary>
public interface IRolloutAlertEditor
{
    /// <summary>Sets the message of every unresolved alert of <paramref name="eventTypes"/> on the site.</summary>
    /// <param name="eventTypes">The rollout alert event types to correct.</param>
    /// <param name="message">The message they now carry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many alerts changed.</returns>
    Task<int> RewriteOpenAsync(IReadOnlyCollection<string> eventTypes, string message, CancellationToken cancellationToken = default);
}

/// <summary>Edits the site's own alert history, in a site-pinned system scope like the health gate.</summary>
public class RolloutAlertEditor : IRolloutAlertEditor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _siteSlug;

    /// <param name="scopeFactory">DI scope factory.</param>
    /// <param name="siteSlug">Site whose alerts are edited.</param>
    public RolloutAlertEditor(IServiceScopeFactory scopeFactory, string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _scopeFactory = scopeFactory;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
    }

    /// <inheritdoc />
    public async Task<int> RewriteOpenAsync(
        IReadOnlyCollection<string> eventTypes, string message, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(_siteSlug);
        using var system = Identity.SystemScope.Enter(
            scope.ServiceProvider, FirmwareRolloutRepositoryAccessor.SystemActor);

        var alerts = scope.ServiceProvider.GetRequiredService<IAlertRepository>();
        var changed = Rewrite(await alerts.GetUnresolvedAlertsAsync(cancellationToken), eventTypes, message);
        foreach (var alert in changed)
            await alerts.UpdateAlertAsync(alert, cancellationToken);
        return changed.Count;
    }

    /// <summary>
    /// Sets <paramref name="message"/> on each unresolved (active or acknowledged) alert of
    /// <paramref name="eventTypes"/> and returns the ones that changed.
    /// </summary>
    /// <param name="alerts">The site's alerts.</param>
    /// <param name="eventTypes">The event types to correct.</param>
    /// <param name="message">The new message.</param>
    public static List<AlertHistoryEntry> Rewrite(
        IEnumerable<AlertHistoryEntry> alerts, IReadOnlyCollection<string> eventTypes, string message)
    {
        var changed = new List<AlertHistoryEntry>();
        foreach (var alert in alerts)
        {
            if (alert.Status == AlertStatus.Resolved || !eventTypes.Contains(alert.EventType) || alert.Message == message)
                continue;
            alert.Message = message;
            changed.Add(alert);
        }
        return changed;
    }
}
