namespace NetworkOptimizer.Web.Services;

/// <inheritdoc />
/// <remarks>
/// Forwards to the site's collection agent rather than being implemented by it: the agent is
/// injected concretely elsewhere, and a class behind a gated interface may not be.
/// </remarks>
public class SfpModulePollService : ISfpModulePollService
{
    private readonly MonitoringCollectionRegistry _registry;
    private readonly SiteContextService _siteContext;

    public SfpModulePollService(MonitoringCollectionRegistry registry, SiteContextService siteContext)
    {
        _registry = registry;
        _siteContext = siteContext;
    }

    /// <inheritdoc />
    public Task<string?> PollModuleAsync(string deviceMac, string portName, CancellationToken ct = default) =>
        _registry.GetFor(_siteContext.Slug).PollModuleAsync(deviceMac, portName, ct);
}
