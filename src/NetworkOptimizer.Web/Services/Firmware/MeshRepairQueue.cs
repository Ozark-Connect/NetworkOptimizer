using System.Text.RegularExpressions;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// Queues the mesh backhaul re-scan a mesh pair needs once both halves have been upgraded. The
/// re-scan runs in the background so the rest of the waves keep moving while it happens.
/// </summary>
public interface IMeshRepairQueue
{
    /// <summary>
    /// Queues a re-scan. Returns false when the request cannot be run at all (no address, or an
    /// interface that is not a mesh STA backhaul), which is a plan-note condition rather than a
    /// rollout failure.
    /// </summary>
    /// <param name="childIp">Mesh child's address.</param>
    /// <param name="iface">Mesh STA backhaul interface, e.g. "vwiresta0".</param>
    /// <param name="apName">AP name, for the audit record and logs.</param>
    bool Enqueue(string? childIp, string? iface, string? apName);
}

/// <summary>
/// The real queue: runs <see cref="IMeshOptimizationService.OptimizeAsync"/> on a background task
/// under the rollout's system caller context, one at a time so two re-scans never overlap.
/// </summary>
public partial class MeshRepairQueue : IMeshRepairQueue
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MeshRepairQueue> _logger;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly string _siteSlug;

    /// <param name="scopeFactory">DI scope factory.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="siteSlug">Site the APs belong to.</param>
    public MeshRepairQueue(
        IServiceScopeFactory scopeFactory,
        ILogger<MeshRepairQueue> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
    }

    /// <summary>Mesh STA backhaul interface names. The optimization service validates this too.</summary>
    [GeneratedRegex(@"^vwiresta\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex MeshStaInterface();

    /// <inheritdoc />
    public bool Enqueue(string? childIp, string? iface, string? apName)
    {
        if (string.IsNullOrWhiteSpace(childIp) || string.IsNullOrWhiteSpace(iface) || !MeshStaInterface().IsMatch(iface))
        {
            _logger.LogDebug(
                "Not queueing a mesh re-pair for {Ap} on site {Site}: address {Ip} / interface {Iface} is not usable",
                apName ?? "unknown", _siteSlug, childIp ?? "none", iface ?? "none");
            return false;
        }

        _ = Task.Run(() => RunAsync(childIp, iface, apName));
        return true;
    }

    private async Task RunAsync(string childIp, string iface, string? apName)
    {
        await _oneAtATime.WaitAsync();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(_siteSlug);
            using var system = Identity.SystemScope.Enter(
                scope.ServiceProvider, FirmwareRolloutRepositoryAccessor.SystemActor);

            var mesh = scope.ServiceProvider.GetRequiredService<IMeshOptimizationService>();
            var result = await mesh.OptimizeAsync(childIp, iface, apName);
            _logger.LogInformation(
                "Mesh re-pair for {Ap} on site {Site} finished: {Action} ({Message})",
                apName ?? childIp, _siteSlug, result.Action, result.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mesh re-pair for {Ap} on site {Site} failed", apName ?? childIp, _siteSlug);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }
}
