using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// Owns all per-site <see cref="FirmwareRolloutOrchestrator"/> instances and their lifecycles.
/// The default site's executor starts with the app unless license enforcement has restricted the
/// site; non-default instances start and stop on a reconcile cadence against the site registry and
/// per-site license state, so adding, enabling, disabling, or re-licensing a site takes effect
/// without a restart. Same ownership pattern as <see cref="MonitoringCollectionRegistry"/>.
///
/// The reconcile pass also nudges every running executor to start any plan whose scheduled time
/// has come, so an overnight rollout begins even on a site whose page nobody has open.
/// </summary>
public class FirmwareRolloutRegistry : BackgroundService, ISiteScopedRegistry
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _serviceProvider;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly Licensing.LicenseStateService _licenseState;
    private readonly ILogger<FirmwareRolloutRegistry> _logger;
    private readonly ConcurrentDictionary<string, FirmwareRolloutOrchestrator> _instances = new(StringComparer.OrdinalIgnoreCase);
    // Slugs whose executor is currently running. Guarded by _lifecycleLock so a reconcile pass and
    // shutdown never race a start/stop.
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    public FirmwareRolloutRegistry(
        IServiceProvider serviceProvider,
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        Licensing.LicenseStateService licenseState,
        ILogger<FirmwareRolloutRegistry> logger)
    {
        _serviceProvider = serviceProvider;
        _mainDbFactory = mainDbFactory;
        _licenseState = licenseState;
        _logger = logger;
    }

    /// <summary>
    /// The rollout executor for a site, created on first use. Creation does not start its loop -
    /// only the reconcile pass does that, so viewing a disabled site's page never starts one.
    /// </summary>
    public FirmwareRolloutOrchestrator GetFor(string slug) =>
        _instances.GetOrAdd(slug, BuildFor);

    /// <summary>The default site's rollout executor.</summary>
    public FirmwareRolloutOrchestrator GetDefault() => GetFor(SiteManagementService.DefaultSiteSlug);

    private FirmwareRolloutOrchestrator BuildFor(string slug)
    {
        // Every collaborator is per-site and is built here rather than resolved, because the
        // orchestrator outlives any scope and each of these captures the site it was made for.
        var bus = new SiteAlertEventBus(_serviceProvider.GetRequiredService<IAlertEventBus>(), slug);
        var repositories = ActivatorUtilities.CreateInstance<FirmwareRolloutRepositoryAccessor>(_serviceProvider, slug);
        var commands = ActivatorUtilities.CreateInstance<FirmwareCommandClient>(_serviceProvider, slug);
        var observer = ActivatorUtilities.CreateInstance<RolloutDeviceObserver>(_serviceProvider, slug);
        var litmus = ActivatorUtilities.CreateInstance<LitmusService>(_serviceProvider, slug);
        var health = ActivatorUtilities.CreateInstance<RolloutHealthGate>(_serviceProvider, slug);
        var meshRepairs = ActivatorUtilities.CreateInstance<MeshRepairQueue>(_serviceProvider, slug);
        var channels = ActivatorUtilities.CreateInstance<RolloutChannelManager>(_serviceProvider, slug, commands);

        return ActivatorUtilities.CreateInstance<FirmwareRolloutOrchestrator>(
            _serviceProvider, slug, repositories, commands, observer, litmus, health, meshRepairs, channels, bus);
    }

    /// <summary>
    /// Stops and forgets a site's executor immediately (site disabled or removed) instead of
    /// waiting for the next reconcile pass - a removal deletes the site's database files right
    /// after, so its loop must be down first.
    /// </summary>
    public async Task StopForSiteAsync(string slug, CancellationToken ct = default)
    {
        await StopInstanceAsync(slug, ct);
        _instances.TryRemove(slug, out _);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The loop has to come down before anything it reads from is disposed, so the stop runs in
    /// the teardown callback and this registry is swept first.
    /// </remarks>
    public Func<ValueTask>? EvictSite(string slug)
        => () => new ValueTask(StopForSiteAsync(slug));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_licenseState.IsSiteOperational(SiteManagementService.DefaultSiteSlug))
            await StartInstanceAsync(SiteManagementService.DefaultSiteSlug, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
                await DriveScheduledPlansAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Per-site firmware rollout reconcile failed");
            }

            try { await Task.Delay(ReconcileInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop the reconcile loop first so it can't restart an instance mid-shutdown, then stop
        // every running site instance.
        await base.StopAsync(cancellationToken);

        List<string> running;
        await _lifecycleLock.WaitAsync(cancellationToken);
        try { running = _running.ToList(); }
        finally { _lifecycleLock.Release(); }

        foreach (var slug in running)
            await StopInstanceAsync(slug, cancellationToken);
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_licenseState.IsSiteOperational(SiteManagementService.DefaultSiteSlug))
            desired.Add(SiteManagementService.DefaultSiteSlug);

        await using (var db = await _mainDbFactory.CreateDbContextAsync(ct))
        {
            var setting = await db.SystemSettings.FindAsync(
                new object[] { SystemSettingKeys.MultiSiteEnabled }, ct);
            if (bool.TryParse(setting?.Value, out var enabled) && enabled)
            {
                var slugs = await db.Sites.AsNoTracking()
                    .Where(s => s.Enabled && !s.IsDefault)
                    .Select(s => s.Slug)
                    .ToListAsync(ct);
                foreach (var slug in slugs.Where(_licenseState.IsSiteOperational))
                    desired.Add(slug);
            }
        }

        foreach (var slug in desired)
            await StartInstanceAsync(slug, ct);

        List<string> toStop;
        await _lifecycleLock.WaitAsync(ct);
        try { toStop = _running.Where(s => !desired.Contains(s)).ToList(); }
        finally { _lifecycleLock.Release(); }

        foreach (var slug in toStop)
            await StopInstanceAsync(slug, ct);
    }

    /// <summary>
    /// Starts anything whose scheduled time has come, and gives autopilot its chance to build the
    /// next plan (Phase 7 fills that in). One site's failure never stops the others.
    /// </summary>
    private async Task DriveScheduledPlansAsync(CancellationToken ct)
    {
        List<string> running;
        await _lifecycleLock.WaitAsync(ct);
        try { running = _running.ToList(); }
        finally { _lifecycleLock.Release(); }

        foreach (var slug in running)
        {
            try
            {
                var orchestrator = GetFor(slug);
                await orchestrator.CreateAutopilotPlanIfDueAsync(ct);
                await orchestrator.StartDueScheduledPlansAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not drive scheduled firmware rollouts for site {Slug}", slug);
            }
        }
    }

    private async Task StartInstanceAsync(string slug, CancellationToken ct)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            if (_running.Contains(slug)) return;
            var instance = GetFor(slug);
            // CancellationToken.None: the token passed here becomes linked into the instance's
            // stopping token, and this one only guards startup.
            await instance.StartAsync(CancellationToken.None);
            _running.Add(slug);
            if (slug != SiteManagementService.DefaultSiteSlug)
                _logger.LogInformation("Started firmware rollout execution for site {Slug}", slug);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StopInstanceAsync(string slug, CancellationToken ct)
    {
        FirmwareRolloutOrchestrator? instance = null;
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            if (!_running.Remove(slug)) return;
            _instances.TryGetValue(slug, out instance);
        }
        finally
        {
            _lifecycleLock.Release();
        }

        if (instance == null) return;
        try
        {
            await instance.StopAsync(ct);
            _logger.LogInformation("Stopped firmware rollout execution for site {Slug}", slug);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop firmware rollout execution for site {Slug}", slug);
        }
    }
}
