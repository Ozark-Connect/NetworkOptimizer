using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Owns all per-site <see cref="MonitoringCollectionAgent"/> instances and their
/// lifecycles. The default site's instance starts with the app (like the old
/// single hosted service) unless license enforcement has restricted the site;
/// non-default instances start and stop on a reconcile cadence against the
/// site registry and per-site license state, so adding, enabling, disabling,
/// or re-licensing a site takes effect without a restart. Scoped resolution of
/// MonitoringCollectionAgent forwards to the current site's instance (the
/// Monitoring page's SNMP status panel reads whichever site it is showing).
/// Same ownership pattern as SiteConnectionRegistry / MonitoringInfluxRegistry.
/// </summary>
public class MonitoringCollectionRegistry : BackgroundService, ISiteScopedRegistry
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _serviceProvider;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly Licensing.LicenseStateService _licenseState;
    private readonly ILogger<MonitoringCollectionRegistry> _logger;
    private readonly ConcurrentDictionary<string, MonitoringCollectionAgent> _instances = new(StringComparer.OrdinalIgnoreCase);
    // Slugs whose instance's collection loops are currently running. Guarded by
    // _lifecycleLock so a reconcile pass and shutdown never race a start/stop.
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly Dictionary<string, SpeedTestBackfillState> _backfills = new(StringComparer.OrdinalIgnoreCase);

    public MonitoringCollectionRegistry(
        IServiceProvider serviceProvider,
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        Licensing.LicenseStateService licenseState,
        ILogger<MonitoringCollectionRegistry> logger)
    {
        _serviceProvider = serviceProvider;
        _mainDbFactory = mainDbFactory;
        _licenseState = licenseState;
        _logger = logger;
    }

    /// <summary>
    /// The collection agent for a site, created on first use. Creation does not
    /// start the collection loops - only the reconcile pass does that, so viewing
    /// a disabled site's Monitoring page never kicks off polling for it.
    /// </summary>
    public MonitoringCollectionAgent GetFor(string slug) =>
        _instances.GetOrAdd(slug, s =>
            ActivatorUtilities.CreateInstance<MonitoringCollectionAgent>(_serviceProvider, s));

    /// <summary>The default site's collection agent.</summary>
    public MonitoringCollectionAgent GetDefault() => GetFor(SiteManagementService.DefaultSiteSlug);

    /// <summary>
    /// Stops and forgets a site's collection instance immediately (site disabled or
    /// removed) instead of waiting for the next reconcile pass - a removal deletes
    /// the site's database files right after, so its loops must be down first.
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
        // The default site collects from startup (pre-multi-site behavior) unless
        // license enforcement has restricted it. Pre-compute the gate reads
        // operational, so startup is never blocked on licensing.
        if (_licenseState.IsSiteOperational(SiteManagementService.DefaultSiteSlug))
            await StartInstanceAsync(SiteManagementService.DefaultSiteSlug, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Per-site collection reconcile failed");
            }

            try { await Task.Delay(ReconcileInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop the reconcile loop first so it can't restart an instance mid-shutdown,
        // then stop every running site instance.
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

    private async Task StartInstanceAsync(string slug, CancellationToken ct)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            if (_running.Contains(slug))
            {
                ScheduleSpeedTestBackfill(slug);
                return;
            }
            var instance = GetFor(slug);
            // CancellationToken.None: the token passed here becomes linked into the
            // instance's stopping token, and this one only guards startup.
            await instance.StartAsync(CancellationToken.None);
            _running.Add(slug);
            ScheduleSpeedTestBackfill(slug);
            if (slug != SiteManagementService.DefaultSiteSlug)
                _logger.LogInformation("Started monitoring collection for site {Slug}", slug);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StopInstanceAsync(string slug, CancellationToken ct)
    {
        MonitoringCollectionAgent? instance = null;
        SpeedTestBackfillState? backfill = null;
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            if (!_running.Remove(slug)) return;
            _instances.TryGetValue(slug, out instance);
            _backfills.Remove(slug, out backfill);
        }
        finally
        {
            _lifecycleLock.Release();
        }

        if (backfill != null)
        {
            await backfill.Cancellation.CancelAsync();
            await backfill.Task;
            backfill.Cancellation.Dispose();
        }
        if (instance == null) return;
        try
        {
            await instance.StopAsync(ct);
            _logger.LogInformation("Stopped monitoring collection for site {Slug}", slug);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop monitoring collection for site {Slug}", slug);
        }
    }

    private void ScheduleSpeedTestBackfill(string slug)
    {
        if (!_backfills.TryGetValue(slug, out var state))
        {
            state = new SpeedTestBackfillState();
            _backfills.Add(slug, state);
        }
        if (!state.Task.IsCompleted) return;
        state.Task = Task.Run(async () =>
        {
            var ct = state.Cancellation.Token;
            try
            {
                var client = _serviceProvider.GetRequiredService<MonitoringInfluxRegistry>().GetFor(slug);
                var version = client.ConfigurationVersion;
                if (state.ConfigurationVersion == version
                    && ((state.Completed && client.IsConfigured) || DateTime.UtcNow < state.RetryAfter))
                    return;

                state.ConfigurationVersion = version;
                await client.ReconfigureAsync(ct, refreshSpeedTestBackfill: false);
                state.ConfigurationVersion = client.ConfigurationVersion;
                state.Completed = await client.BackfillSpeedTestResultsAsync(ct);
                state.RetryAfter = DateTime.UtcNow.AddMinutes(5);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                state.Completed = false;
                state.RetryAfter = DateTime.UtcNow.AddMinutes(5);
                _logger.LogWarning(ex, "Speed test InfluxDB backfill failed for site {Site}; will retry", slug);
            }
        });
    }

    private sealed class SpeedTestBackfillState
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;
        public long ConfigurationVersion { get; set; } = -1;
        public bool Completed { get; set; }
        public DateTime RetryAfter { get; set; }
    }
}
