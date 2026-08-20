using NetworkOptimizer.Storage.Interfaces;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// Hands the orchestrator a rollout repository bound to its own site.
/// <para>
/// The orchestrator is a long-lived per-site singleton and the repository is scoped, so every read
/// and write opens its own scope. Pinning the site and entering the system caller scope belongs in
/// one place rather than at each of the dozens of call sites, and a fake of this interface is what
/// lets the state machine be tested against a plain in-memory repository.
/// </para>
/// </summary>
public interface IFirmwareRolloutRepositoryAccessor
{
    /// <summary>Runs work against the site's rollout repository and returns its result.</summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="work">What to do with the repository.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<T> UseAsync<T>(Func<IFirmwareRolloutRepository, CancellationToken, Task<T>> work, CancellationToken cancellationToken = default);

    /// <summary>Runs work against the site's rollout repository.</summary>
    /// <param name="work">What to do with the repository.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UseAsync(Func<IFirmwareRolloutRepository, CancellationToken, Task> work, CancellationToken cancellationToken = default);
}

/// <summary>
/// The real accessor: a DI scope pinned to this site, marked as the rollout system actor so gated
/// services invoked underneath it are authorized and audited rather than refused.
/// </summary>
public class FirmwareRolloutRepositoryAccessor : IFirmwareRolloutRepositoryAccessor
{
    /// <summary>Audit actor name for everything the executor does on its own.</summary>
    public const string SystemActor = "rollout:firmware";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _siteSlug;

    /// <param name="scopeFactory">DI scope factory.</param>
    /// <param name="siteSlug">Site whose database the repository must bind to.</param>
    public FirmwareRolloutRepositoryAccessor(
        IServiceScopeFactory scopeFactory,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _scopeFactory = scopeFactory;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
    }

    /// <inheritdoc />
    public async Task<T> UseAsync<T>(
        Func<IFirmwareRolloutRepository, CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(_siteSlug);
        using var system = Identity.SystemScope.Enter(scope.ServiceProvider, SystemActor);
        var repository = scope.ServiceProvider.GetRequiredService<IFirmwareRolloutRepository>();
        return await work(repository, cancellationToken);
    }

    /// <inheritdoc />
    public async Task UseAsync(
        Func<IFirmwareRolloutRepository, CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        await UseAsync<object?>(async (repository, ct) =>
        {
            await work(repository, ct);
            return null;
        }, cancellationToken);
    }
}
