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
        var planning = ActivatorUtilities.CreateInstance<RolloutPlanningScope>(_serviceProvider, slug);
        var autopilot = ActivatorUtilities.CreateInstance<RolloutAutopilot>(
            _serviceProvider, slug, repositories, planning, commands, bus);

        // The site's Influx client is scoped and the orchestrator outlives any scope, so the witness
        // is built here against this site's client like every other collaborator above.
        var rebootWitness = new InfluxRolloutRebootWitness(
            _serviceProvider.GetRequiredService<MonitoringInfluxRegistry>().GetFor(slug),
            _serviceProvider.GetRequiredService<ILogger<InfluxRolloutRebootWitness>>());

        return ActivatorUtilities.CreateInstance<FirmwareRolloutOrchestrator>(
            _serviceProvider, slug, repositories, commands, observer, litmus, health, meshRepairs, channels,
            autopilot, bus, rebootWitness);
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

        await BackfillSharedCatalogAsync(stoppingToken);

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

    private async Task BackfillSharedCatalogAsync(CancellationToken ct)
    {
        try
        {
            var catalog = _serviceProvider.GetService<NetworkOptimizer.Storage.Interfaces.ISharedFirmwareCatalogRepository>();
            if (catalog == null) return;

            var deviceBuilds = new List<SharedFirmwareBuild>();
            var appBuilds = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            // Main DB (also the default site's data)
            List<string> slugs;
            bool seedNetworkApp;
            await using (var db = await _mainDbFactory.CreateDbContextAsync(ct))
            {
                // Network app rows only on the very first startup: extraction INFERS the channel
                // from the version (plans never recorded it), which goes stale as GA advances.
                // Once the table has rows, the live gather records real channels; re-inferring
                // here would plant wrong-channel rows on every startup.
                seedNetworkApp = !await db.SharedNetworkAppBuilds.AsNoTracking().AnyAsync(ct);

                var plans = await db.FirmwareRolloutPlans.AsNoTracking()
                    .Where(p => p.PlanJson != null)
                    .Select(p => p.PlanJson!)
                    .ToListAsync(ct);
                foreach (var json in plans)
                    ExtractCatalogEntries(json, deviceBuilds, seedNetworkApp ? appBuilds : null);

                slugs = await db.Sites.AsNoTracking()
                    .Where(s => s.Enabled && s.Slug != SiteManagementService.DefaultSiteSlug)
                    .Select(s => s.Slug)
                    .ToListAsync(ct);
            }

            // Site DBs, opened by explicit path. The injectable IDbContextFactory is a singleton
            // bound to the main DB, so resolving it under an OverrideSite scope silently reads
            // main again - which is how site plans were missed entirely.
            var siteDbs = _serviceProvider.GetRequiredService<NetworkOptimizer.Storage.Services.SiteDbContextFactory>();
            foreach (var slug in slugs)
            {
                try
                {
                    if (!siteDbs.SiteDbExists(slug)) continue;
                    await using var siteDb = siteDbs.CreateForSite(slug);
                    var plans = await siteDb.FirmwareRolloutPlans.AsNoTracking()
                        .Where(p => p.PlanJson != null)
                        .Select(p => p.PlanJson!)
                        .ToListAsync(ct);
                    foreach (var json in plans)
                        ExtractCatalogEntries(json, deviceBuilds, seedNetworkApp ? appBuilds : null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Skipping site {Slug} during shared catalog backfill", slug);
                }
            }

            if (deviceBuilds.Count > 0)
                await catalog.UpsertDeviceBuildsAsync(deviceBuilds, ct);

            foreach (var (key, url) in appBuilds)
            {
                var parts = key.Split('|', 2);
                await catalog.UpsertNetworkAppBuildAsync(parts[0], parts[1], url, ct);
            }

            _logger.LogInformation(
                "Shared firmware catalog backfill: {Devices} device builds, {Apps} Network app builds",
                deviceBuilds.Count, appBuilds.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Shared firmware catalog backfill failed");
        }
    }

    /// <summary>
    /// Pulls catalog entries out of one plan document: device builds from TargetImages (model from
    /// wave steps, channel from the channel group covering the device's wave) and, when
    /// <paramref name="appBuilds"/> is given, the Network application build from NetworkAppUpdate.
    /// </summary>
    private static void ExtractCatalogEntries(
        string json, List<SharedFirmwareBuild> deviceBuilds, Dictionary<string, string?>? appBuilds)
    {
        if (string.IsNullOrWhiteSpace(json)) return;

        RolloutPlanDocument? document;
        try
        {
            document = System.Text.Json.JsonSerializer.Deserialize<RolloutPlanDocument>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }
        if (document == null) return;

        var modelByMac = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var waveByMac = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var wave in document.Waves)
            foreach (var step in wave.Steps)
            {
                if (string.IsNullOrEmpty(step.Mac)) continue;
                if (!string.IsNullOrEmpty(step.Model))
                    modelByMac[step.Mac] = step.Model;
                waveByMac[step.Mac] = wave.Number;
            }

        foreach (var img in document.TargetImages)
        {
            if (string.IsNullOrEmpty(img.Mac) || string.IsNullOrEmpty(img.Version) || string.IsNullOrEmpty(img.Url))
                continue;
            if (!modelByMac.TryGetValue(img.Mac, out var model) || string.IsNullOrEmpty(model))
                continue;
            waveByMac.TryGetValue(img.Mac, out var waveNum);
            var channel = document.ChannelGroups
                .FirstOrDefault(g => waveNum >= g.FirstWave && waveNum <= g.LastWave)?.Channel;
            if (string.IsNullOrEmpty(channel)) continue;

            deviceBuilds.Add(new SharedFirmwareBuild
            {
                Model = model, Channel = channel, Version = img.Version, Url = img.Url,
            });
        }

        if (appBuilds != null
            && document.IncludesUniFiNetworkUpdate
            && document.NetworkAppUpdate.TargetVersion is { Length: > 0 } ver)
        {
            // First-startup seed only (appBuilds is null after that): the channel is inferred
            // against a GA version frozen at the time this shipped, so it cannot stay right.
            var channel = NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.IsNewer(ver, "10.5.67") ? "beta" : "release";
            var key = $"{channel}|{ver}";
            var url = document.NetworkAppUpdate.Url;
            if (!appBuilds.ContainsKey(key) || !string.IsNullOrEmpty(url))
                appBuilds[key] = url;
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
