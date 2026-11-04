using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>Whether the site is in a fit state to start taking devices down.</summary>
/// <param name="Healthy">True when nothing objects to starting.</param>
/// <param name="Reason">What is wrong, for the postpone alert.</param>
public sealed record RolloutHealthVerdict(bool Healthy, string? Reason = null)
{
    /// <summary>Nothing objects.</summary>
    public static RolloutHealthVerdict Ok() => new(true);

    /// <summary>Something objects.</summary>
    public static RolloutHealthVerdict Blocked(string reason) => new(false, reason);
}

/// <summary>
/// The start-time health check. A scheduled or autopilot run defers to the next window when the
/// site is already in trouble; a Site Admin starting one by hand can override it.
/// </summary>
public interface IRolloutHealthGate
{
    /// <summary>Evaluates the site's current health.</summary>
    /// <param name="openedSince">
    /// Counts only critical alerts opened at or after this instant (the plan's booking), or every open
    /// critical alert when null.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<RolloutHealthVerdict> EvaluateAsync(DateTime? openedSince, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the site's own active alerts. One read covers both halves of the requirement: an ongoing
/// ISP Health outage is published as an active WAN outage alert, so a site that is down shows up
/// here without a second source to keep in step.
/// </summary>
public class RolloutHealthGate : IRolloutHealthGate
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RolloutHealthGate> _logger;
    private readonly string _siteSlug;

    /// <param name="scopeFactory">DI scope factory.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="siteSlug">Site to evaluate.</param>
    public RolloutHealthGate(
        IServiceScopeFactory scopeFactory,
        ILogger<RolloutHealthGate> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
    }

    /// <inheritdoc />
    public async Task<RolloutHealthVerdict> EvaluateAsync(DateTime? openedSince, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(_siteSlug);
            using var system = Identity.SystemScope.Enter(
                scope.ServiceProvider, FirmwareRolloutRepositoryAccessor.SystemActor);

            var alerts = scope.ServiceProvider.GetRequiredService<IAlertRepository>();
            return Judge(await alerts.GetActiveAlertsAsync(cancellationToken), openedSince);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not being able to read the site's health is not evidence that it is unhealthy, and a
            // read that keeps throwing must not park an autopilot rollout forever.
            _logger.LogWarning(ex, "Could not read active alerts for the rollout health gate on site {Site}", _siteSlug);
            return RolloutHealthVerdict.Ok();
        }
    }

    /// <summary>
    /// The verdict over a site's alerts: blocked by any active (not acknowledged) critical alert,
    /// newest first in the reason. A critical opened before <paramref name="openedSince"/> was already
    /// open when the rollout was booked, so it does not hold the start: a stale one would postpone the
    /// rollout every window for as long as nobody closed it.
    /// </summary>
    /// <param name="alerts">The site's alerts.</param>
    /// <param name="openedSince">Earliest open time that counts, or null for every open critical alert.</param>
    public static RolloutHealthVerdict Judge(IEnumerable<AlertHistoryEntry> alerts, DateTime? openedSince)
    {
        var criticals = alerts
            .Where(a => a.Severity == AlertSeverity.Critical && a.Status == AlertStatus.Active)
            .Where(a => openedSince is not { } since || a.TriggeredAt >= since)
            .OrderByDescending(a => a.TriggeredAt)
            .ToList();

        if (criticals.Count == 0)
            return RolloutHealthVerdict.Ok();

        var first = criticals[0].Title;
        var reason = criticals.Count == 1
            ? $"a critical alert is open ({first})"
            : $"{criticals.Count} critical alerts are open (including {first})";
        return RolloutHealthVerdict.Blocked(reason);
    }
}
