using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.Auditing;
using NetworkOptimizer.Web.Services.Firmware;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>
/// Owns one <see cref="ChangeControlSiteEngine"/> per site. The default site's engine starts with
/// the app; other sites start and stop on a reconcile against the site registry and license
/// state, the same terms as Firmware Rollout.
/// </summary>
public sealed class ChangeControlRegistry : BackgroundService, ISiteScopedRegistry
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _sp;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly Licensing.LicenseStateService _licenseState;
    private readonly ILogger<ChangeControlRegistry> _logger;
    private readonly ConcurrentDictionary<string, ChangeControlSiteEngine> _engines = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PlanCoordinator> _coordinators = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    public ChangeControlRegistry(IServiceProvider sp, IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        Licensing.LicenseStateService licenseState, ILogger<ChangeControlRegistry> logger)
    {
        _sp = sp;
        _mainDbFactory = mainDbFactory;
        _licenseState = licenseState;
        _logger = logger;
    }

    /// <summary>Raised with a site's slug after it records a snapshot or change, so open pages can refresh.</summary>
    public event Action<string>? Changed;

    /// <summary>The engine for a site, created on first use (not started until reconcile).</summary>
    public ChangeControlSiteEngine GetFor(string slug) => _engines.GetOrAdd(slug, Build);

    /// <summary>The console connection of a site.</summary>
    public UniFiConnectionService Connection(string slug) => _sp.GetRequiredService<SiteConnectionRegistry>().GetFor(slug);

    /// <summary>The encrypted snapshot and file store.</summary>
    public IConfigBlobStore Blobs => _sp.GetRequiredService<IConfigBlobStore>();

    /// <summary>Bytes on disk for a site's snapshot blobs.</summary>
    public long BlobBytes(string slug) => _sp.GetRequiredService<IConfigBlobStore>().GetStorageBytes(slug).BlobBytes;

    /// <summary>Bytes on disk for a site's snapshot blobs and console backups.</summary>
    public (long Blobs, long Files) StorageBytes(string slug) => _sp.GetRequiredService<IConfigBlobStore>().GetStorageBytes(slug);

    /// <summary>The plan coordinator for a site.</summary>
    public PlanCoordinator PlansFor(string slug)
    {
        GetFor(slug);
        return _coordinators[slug];
    }

    /// <summary>Network Optimizer's own settings of a site that a re-address moves; saved when <paramref name="apply"/>.</summary>
    public async Task<List<SettingRewrite>> ReaddressSettingsAsync(string slug, IReadOnlyList<SubnetMove> moves, IReadOnlyList<string> remote, bool apply, CancellationToken ct)
    {
        await using var scope = _sp.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(slug);
        using var system = Identity.SystemScope.Enter(scope.ServiceProvider, ChangeControlRepositoryAccessor.SystemActor);
        var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
        return await ReaddressSettings.RunAsync(db, moves, remote, apply, ct);
    }

    /// <summary>Records one of NO's console writes against its site's journal.</summary>
    public void Journal(string slug, UniFiWriteRecord record) => GetFor(slug).Journal(record);

    /// <summary>This server's own unicast addresses (a direct site reaches the console from here).</summary>
    private static IReadOnlyList<string> ServerAddresses()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address.ToString()).ToList();
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return [];
        }
    }

    private ChangeControlSiteEngine Build(string slug)
    {
        var connection = _sp.GetRequiredService<SiteConnectionRegistry>().GetFor(slug);
        var rollout = ActivatorUtilities.CreateInstance<FirmwareRolloutRepositoryAccessor>(_sp, slug);
        var engine = new ChangeControlSiteEngine(
            slug,
            new SiteConfigApi(connection),
            ActivatorUtilities.CreateInstance<ChangeControlRepositoryAccessor>(_sp, slug),
            _sp.GetRequiredService<IConfigBlobStore>(),
            _sp.GetRequiredService<ILoggerFactory>().CreateLogger($"ChangeControl[{slug}]"),
            _sp.GetService<IAuditLogger>(),
            isRolloutActive: async ct => await rollout.UseAsync(async (r, c) => await r.GetActivePlanAsync(c) is { Status: FirmwareRolloutStatus.Running }, ct),
            isAgentSite: () => connection.IsViaAgent || connection.IsAwaitingAgent);
        engine.Evaluator = _sp.GetService<IChangeEvaluator>();
        engine.Changed += () => Changed?.Invoke(slug);
        var history = new FirmwareHistoryImport(rollout, () => _sp.GetRequiredService<MonitoringInfluxRegistry>().GetFor(slug),
            _sp.GetRequiredService<ILoggerFactory>().CreateLogger($"ChangeControl[{slug}]"));
        engine.ImportHistory = (names, ct) => history.RunAsync(engine, names, ct);

        var litmus = ActivatorUtilities.CreateInstance<LitmusService>(_sp, slug);
        var coordinator = new PlanCoordinator(engine, _sp.GetRequiredService<ICredentialProtectionService>(),
            _sp.GetRequiredService<ILoggerFactory>().CreateLogger($"ChangeControlPlans[{slug}]"),
            engine.Evaluator, _sp.GetService<NetworkOptimizer.Alerts.Events.IAlertEventBus>(), _sp.GetService<IAuditLogger>(),
            async (mac, from, to, ct) => await litmus.CaptureStatsAsync(mac, from, to, ct))
        {
            IsAgentSite = () => connection.IsViaAgent,
            ConsoleHost = () => Uri.TryCreate(connection.CurrentConfig?.ControllerUrl, UriKind.Absolute, out var u) ? u.Host : null,
        };
        var agents = _sp.GetService<AgentTunnelRegistry>();
        if (agents != null)
            coordinator.AgentRevert = new AgentRevertArmer(slug, engine, connection, agents,
                _sp.GetRequiredService<ILoggerFactory>().CreateLogger($"ChangeControlAgentRevert[{slug}]"));
        coordinator.ManagementAddresses = () => connection.IsViaAgent && agents != null
            ? agents.GetForSite(slug).SelectMany(a => a.LocalIps.Append(a.LanIp ?? "")).Where(a => a.Length > 0).Distinct().ToList()
            : ServerAddresses();
        coordinator.MoveSettings = (moves, remote, ct) => ReaddressSettingsAsync(slug, moves, remote, apply: true, ct);
        _coordinators[slug] = coordinator;
        var console = new ConsoleWatch(engine, _sp.GetRequiredService<IConfigBlobStore>(), _sp.GetService<NetworkOptimizer.Alerts.Events.IAlertEventBus>(),
            _sp.GetRequiredService<ILoggerFactory>().CreateLogger($"ChangeControlConsole[{slug}]"));
        var health = new SiteHealth.SiteHealthJob(slug, engine.Repositories,
            () => _sp.GetRequiredService<MonitoringInfluxRegistry>().GetFor(slug),
            () => _sp.GetRequiredService<Monitoring.IspHealth.IspHealthRegistry>().GetFor(slug, null),
            () => connection, _sp.GetService<NetworkOptimizer.Alerts.Events.IAlertEventBus>(),
            _sp.GetRequiredService<ILoggerFactory>().CreateLogger($"SiteHealth[{slug}]"));
        engine.OnTick = async ct =>
        {
            await coordinator.TickAsync(ct);
            await console.TickAsync(ct);
            await health.TickAsync(ct);
        };
        return engine;
    }

    /// <inheritdoc />
    public Func<ValueTask>? EvictSite(string slug) => () => new ValueTask(StopForSiteAsync(slug));

    /// <summary>Stops and forgets a site's engine (site disabled or removed).</summary>
    public async Task StopForSiteAsync(string slug, CancellationToken ct = default)
    {
        await StopAsync(slug, ct);
        _engines.TryRemove(slug, out _);
        _coordinators.TryRemove(slug, out _);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReconcileAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "Change Control reconcile failed"); }

            try { await Task.Delay(ReconcileInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        List<string> running;
        await _lifecycleLock.WaitAsync(cancellationToken);
        try { running = _running.ToList(); }
        finally { _lifecycleLock.Release(); }
        foreach (var slug in running) await StopAsync(slug, cancellationToken);
    }

    /// <summary>Slugs of the sites whose engines are running.</summary>
    public IReadOnlyList<string> RunningSites()
    {
        lock (_running) return _running.ToList();
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_licenseState.IsSiteOperational(SiteManagementService.DefaultSiteSlug))
            desired.Add(SiteManagementService.DefaultSiteSlug);

        await using (var db = await _mainDbFactory.CreateDbContextAsync(ct))
        {
            var setting = await db.SystemSettings.FindAsync([SystemSettingKeys.MultiSiteEnabled], ct);
            if (bool.TryParse(setting?.Value, out var enabled) && enabled)
                foreach (var slug in await db.Sites.AsNoTracking().Where(s => s.Enabled && !s.IsDefault).Select(s => s.Slug).ToListAsync(ct))
                    if (_licenseState.IsSiteOperational(slug)) desired.Add(slug);
        }

        foreach (var slug in desired) await StartAsync(slug, ct);
        List<string> stop;
        await _lifecycleLock.WaitAsync(ct);
        try { stop = _running.Where(s => !desired.Contains(s)).ToList(); }
        finally { _lifecycleLock.Release(); }
        foreach (var slug in stop) await StopAsync(slug, ct);
    }

    private async Task StartAsync(string slug, CancellationToken ct)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            if (_running.Contains(slug)) return;
            await GetFor(slug).StartAsync(CancellationToken.None);
            lock (_running) _running.Add(slug);
        }
        finally { _lifecycleLock.Release(); }
    }

    private async Task StopAsync(string slug, CancellationToken ct)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            bool removed;
            lock (_running) removed = _running.Remove(slug);
            if (!removed || !_engines.TryGetValue(slug, out var engine)) return;
            try { await engine.StopAsync(ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not stop Change Control for site {Site}", slug); }
        }
        finally { _lifecycleLock.Release(); }
    }
}
