namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// Hands a background worker a planning source bound to its own site.
/// <para>
/// Same shape and the same reason as <see cref="IFirmwareRolloutRepositoryAccessor"/>: autopilot is
/// a long-lived per-site singleton and the planning source is scoped, so each use opens its own
/// site-pinned system scope. A fake of this interface is what lets autopilot be tested without a
/// console, a floor plan or the release feed.
/// </para>
/// </summary>
public interface IRolloutPlanningScope
{
    /// <summary>Runs work against the site's planning source and returns its result.</summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="work">What to do with the planning source.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<T> UseAsync<T>(
        Func<IRolloutPlanningSource, CancellationToken, Task<T>> work, CancellationToken cancellationToken = default);

    /// <summary>Runs work against the site's planning source.</summary>
    /// <param name="work">What to do with the planning source.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UseAsync(
        Func<IRolloutPlanningSource, CancellationToken, Task> work, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IRolloutPlanningScope" />
public class RolloutPlanningScope : IRolloutPlanningScope
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _siteSlug;

    /// <param name="scopeFactory">DI scope factory.</param>
    /// <param name="siteSlug">Site the planning source must plan for.</param>
    public RolloutPlanningScope(
        IServiceScopeFactory scopeFactory,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _scopeFactory = scopeFactory;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
    }

    /// <inheritdoc />
    public async Task<T> UseAsync<T>(
        Func<IRolloutPlanningSource, CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(_siteSlug);
        using var system = Identity.SystemScope.Enter(
            scope.ServiceProvider, FirmwareRolloutRepositoryAccessor.SystemActor);
        var planning = scope.ServiceProvider.GetRequiredService<IRolloutPlanningSource>();
        return await work(planning, cancellationToken);
    }

    /// <inheritdoc />
    public async Task UseAsync(
        Func<IRolloutPlanningSource, CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        await UseAsync<object?>(async (planning, ct) =>
        {
            await work(planning, ct);
            return null;
        }, cancellationToken);
    }
}
