using NetworkOptimizer.Alerts.Interfaces;
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
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<RolloutHealthVerdict> EvaluateAsync(CancellationToken cancellationToken = default);
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
    public async Task<RolloutHealthVerdict> EvaluateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(_siteSlug);
            using var system = Identity.SystemScope.Enter(
                scope.ServiceProvider, FirmwareRolloutRepositoryAccessor.SystemActor);

            var alerts = scope.ServiceProvider.GetRequiredService<IAlertRepository>();
            var active = await alerts.GetActiveAlertsAsync(cancellationToken);
            var criticals = active
                .Where(a => a.Severity == AlertSeverity.Critical && a.Status == AlertStatus.Active)
                .ToList();

            if (criticals.Count == 0)
                return RolloutHealthVerdict.Ok();

            var first = criticals[0].Title;
            var reason = criticals.Count == 1
                ? $"a critical alert is open ({first})"
                : $"{criticals.Count} critical alerts are open (including {first})";
            return RolloutHealthVerdict.Blocked(reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not being able to read the site's health is not evidence that it is unhealthy, and a
            // read that keeps throwing must not park an autopilot rollout forever.
            _logger.LogWarning(ex, "Could not read active alerts for the rollout health gate on site {Site}", _siteSlug);
            return RolloutHealthVerdict.Ok();
        }
    }
}
