using NetworkOptimizer.Storage.Interfaces;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>
/// Hands long-lived per-site Change Control components a repository bound to their site, inside
/// the system caller scope (the same shape as the Firmware Rollout accessor).
/// </summary>
public interface IChangeControlRepositoryAccessor
{
    /// <summary>Runs work against the site's repository and returns its result.</summary>
    Task<T> UseAsync<T>(Func<IChangeControlRepository, CancellationToken, Task<T>> work, CancellationToken cancellationToken = default);

    /// <summary>Runs work against the site's repository.</summary>
    Task UseAsync(Func<IChangeControlRepository, CancellationToken, Task> work, CancellationToken cancellationToken = default);
}

/// <summary>The real accessor: a DI scope pinned to the site, marked as the Change Control system actor.</summary>
public sealed class ChangeControlRepositoryAccessor : IChangeControlRepositoryAccessor
{
    /// <summary>Audit actor name for what Change Control does on its own.</summary>
    public const string SystemActor = "change-control";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _siteSlug;

    public ChangeControlRepositoryAccessor(IServiceScopeFactory scopeFactory, string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _scopeFactory = scopeFactory;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
    }

    /// <inheritdoc />
    public async Task<T> UseAsync<T>(Func<IChangeControlRepository, CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(_siteSlug);
        using var system = Identity.SystemScope.Enter(scope.ServiceProvider, SystemActor);
        return await work(scope.ServiceProvider.GetRequiredService<IChangeControlRepository>(), cancellationToken);
    }

    /// <inheritdoc />
    public Task UseAsync(Func<IChangeControlRepository, CancellationToken, Task> work, CancellationToken cancellationToken = default) =>
        UseAsync<object?>(async (r, ct) => { await work(r, ct); return null; }, cancellationToken);
}
