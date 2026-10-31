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
    private readonly ConcurrentDictionary<string, SiteHealth.SiteHealthJob> _health = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>
    /// Raised with a site's slug after its Site Health job rescores the current hour (every few minutes).
    /// Separate from <see cref="Changed"/> so only health lines redraw, not change lists.
    /// </summary>
    public event Action<string>? HealthScored;

    /// <summary>The engine for a site, created on first use (not started until reconcile).</summary>
    public ChangeControlSiteEngine GetFor(string slug) => _engines.GetOrAdd(slug, Build);

    /// <summary>A site's stored health hours plus its provisional current hour, for drawing (never for change checks).</summary>
    public List<SiteHealthHour> WithProvisionalHealth(string slug, IEnumerable<SiteHealthHour> stored)
    {
        GetFor(slug);
        return SiteHealth.SiteHealthJob.WithProvisional(stored, _health.TryGetValue(slug, out var job) ? job.Provisional : []);
    }

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

    /// <summary>
    /// MACs the site's gateway is serving a DHCP lease to (dnsmasq's lease file, over Gateway SSH); null
    /// without Gateway SSH. A connected client missing here set its address on the device, reserved or not.
    /// </summary>
    public async Task<HashSet<string>?> GatewayLeaseMacsAsync(string slug, CancellationToken ct)
    {
        try
        {
            var (ok, output) = await _sp.GetRequiredService<GatewaySshRegistry>().GetFor(slug)
                .RunCommandAsync("cat /data/udapi-config/dnsmasq.lease", TimeSpan.FromSeconds(15), ct);
            if (!ok) return null;
            // "<expiry> <mac> <ip> <hostname> <client-id>" per line.
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(p => p.Length >= 3 && p[1].Count(c => c == ':') == 5)
                .Select(p => p[1].ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Gateway lease file read failed for site {Site}", slug);
            return null;
        }
    }

    /// <summary>Network Optimizer's own settings of a site that a re-address moves; saved when <paramref name="apply"/>.</summary>
    public async Task<List<SettingRewrite>> ReaddressSettingsAsync(string slug, IReadOnlyList<SubnetMove> moves, IReadOnlyList<string> remote, bool apply, CancellationToken ct)
    {
        await using var scope = _sp.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(slug);
        using var system = Identity.SystemScope.Enter(scope.ServiceProvider, ChangeControlRepositoryAccessor.SystemActor);
        var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
        await using var main = await _mainDbFactory.CreateDbContextAsync(ct);
        var isDefault = string.Equals(slug, SiteManagementService.DefaultSiteSlug, StringComparison.OrdinalIgnoreCase);
        var moved = await ReaddressSettings.RunAsync(db, moves, remote, apply, ct, main, isDefault ? null : slug);
        // The live connection caches its settings: reconnect at the new console address now, not in five minutes.
        if (apply && moved.Any(m => m.Setting == ReaddressSettings.ConsoleSetting))
            _ = Connection(slug).ReloadAndReconnectAsync();
        // Every site's InfluxDB client derives from the main site's settings, so each picks the new URL up.
        if (apply && moved.Any(m => m.Setting == ReaddressSettings.InfluxSetting))
            foreach (var site in RunningSites().Append(slug).Distinct(StringComparer.OrdinalIgnoreCase))
                _ = _sp.GetRequiredService<MonitoringInfluxRegistry>().GetFor(site).ReconfigureAsync();
        return moved;
    }

    /// <summary>Records one of NO's console writes against its site's journal.</summary>
    public void Journal(string slug, UniFiWriteRecord record) => GetFor(slug).Journal(record);

    /// <summary>
    /// Where an agent site is reached from: the LAN address of the agent the console and device dials go through, the one
    /// it chose for its own traffic. Not every agent's, nor every address its host holds: a second agent on the gateway
    /// holds every VLAN's gateway address, which made each network look like where Network Optimizer manages from.
    /// An agent too old to report a LAN address gives all it has. An agent on the gateway reaches the console from the
    /// gateway itself, which no policy into the Gateway zone matches: loopback, which the lockout check reads as no LAN
    /// network. Its path to a console elsewhere on the LAN is checked apart (<see cref="PlanCoordinator.GatewayConsole"/>).
    /// </summary>
    internal static IReadOnlyList<string> AgentReachAddresses(AgentTunnelConnection dial) =>
        dial.OnGateway == true ? ["127.0.0.1"]
        : string.IsNullOrWhiteSpace(dial.LanIp) ? dial.HostAddresses : [dial.LanIp!];

    /// <summary>
    /// The console's address and port when its traffic goes through an agent on the gateway: the Console URL's host, a name
    /// resolved by that agent (never by this server, which may see another answer), with the URL's port.
    /// </summary>
    private async Task<(string Address, int Port)?> GatewayConsoleAsync(string slug, UniFiConnectionService connection, AgentTunnelProxyService? proxy, CancellationToken ct)
    {
        if (!connection.IsViaAgent || proxy?.DialAgent(slug) is not { OnGateway: true } dial) return null;
        if (!Uri.TryCreate(connection.CurrentConfig?.ControllerUrl, UriKind.Absolute, out var url)) return null;
        if (System.Net.IPAddress.TryParse(url.Host, out var ip))
            return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? (ip.ToString(), url.Port) : null;
        if (_sp.GetService<AgentProbeService>() is not { } probes) return null;
        var lookup = await new Monitoring.AgentProbeExecutor(probes, slug, _sp.GetRequiredService<ILoggerFactory>().CreateLogger("ChangeControlConsoleLookup"), dial.AgentId)
            .LookupAsync(new NetworkOptimizer.Monitoring.Probes.ProbeTarget(url.Host, NetworkOptimizer.Core.Enums.ProbeMode.Icmp), ct: ct);
        return lookup.Addresses.FirstOrDefault(a => System.Net.IPAddress.TryParse(a, out var r) && r.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) is { } address
            ? (address, url.Port) : null;
    }

    /// <summary>
    /// The addresses this server reaches the console from: the source the OS routes from toward the console
    /// host. Every interface address is not the answer on a host with a leg on many VLANs (the NAS has five):
    /// a policy blocking one of those VLANs from the gateway cannot cut a path that never leaves through it.
    /// Falls back to every address when the host cannot be resolved or routed.
    /// </summary>
    internal static IReadOnlyList<string> ServerAddresses(string? consoleHost)
    {
        if (!string.IsNullOrEmpty(consoleHost))
        {
            try
            {
                var targets = System.Net.IPAddress.TryParse(consoleHost, out var literal) ? [literal]
                    : System.Net.Dns.GetHostAddresses(consoleHost);
                var sources = new List<string>();
                foreach (var target in targets.Where(t => t.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
                {
                    // Connect on UDP only asks the routing table; nothing is sent.
                    using var socket = new System.Net.Sockets.Socket(target.AddressFamily, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
                    socket.Connect(target, 443);
                    if (socket.LocalEndPoint is System.Net.IPEndPoint local) sources.Add(local.Address.ToString());
                }
                if (sources.Count > 0) return sources.Distinct().ToList();
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
            {
                // Unresolvable or unroutable: fall back to every address.
            }
        }
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
            isAgentSite: () => connection.IsViaAgent || connection.IsAwaitingAgent,
            hasNoConsole: () => connection.HasNoConsole,
            // A rollout running, or one that ran in the last hour: the Network app finishes its upgrade after the plan does.
            wasRolloutRecent: async ct => await rollout.UseAsync(async (r, c) =>
                (await r.GetPlanHistoryAsync(5, c)).Any(p => p.Status == FirmwareRolloutStatus.Running
                    || (p.StartedAt != null && p.CompletedAt > DateTime.UtcNow.AddHours(-1))), ct));
        engine.Evaluator = _sp.GetService<IChangeEvaluator>();
        engine.Changed += () => Changed?.Invoke(slug);
        // Online and agent-offline on the Overview follow the connection itself, not the next detection pass.
        connection.OnConnectionChanged += () => Changed?.Invoke(slug);
        var history = new FirmwareHistoryImport(rollout, () => _sp.GetRequiredService<MonitoringInfluxRegistry>().GetFor(slug),
            _sp.GetRequiredService<ILoggerFactory>().CreateLogger($"ChangeControl[{slug}]"));
        engine.ImportHistory = (names, ct) => history.RunAsync(engine, names, ct);
        engine.RolloutFor = history.RolloutForAsync;

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
            : ServerAddresses(null);
        // Narrower than ManagementAddresses, which Re-address also reads for every address the host owns.
        var proxy = _sp.GetService<AgentTunnelProxyService>();
        coordinator.ReachAddresses = () => !connection.IsViaAgent ? ServerAddresses(coordinator.ConsoleHost())
            : proxy?.DialAgent(slug) is { } dial ? AgentReachAddresses(dial)
            : coordinator.ManagementAddresses();
        coordinator.GatewayConsole = ct => GatewayConsoleAsync(slug, connection, proxy, ct);
        coordinator.MoveSettings = (moves, remote, ct) => ReaddressSettingsAsync(slug, moves, remote, apply: true, ct);
        coordinator.ZonesChanged = _ =>
        {
            using var scope = _sp.CreateScope();
            scope.ServiceProvider.GetService<ThreatBlockingOperations>()?.SyncZonesSoon(slug);
            return Task.CompletedTask;
        };
        if (_sp.GetService<RolloutSuppressionRegistry>() is { } suppression)
            coordinator.QuietDevices = () => suppression.RefreshDevicesQuiet(slug, DateTime.UtcNow);
        _coordinators[slug] = coordinator;
        var console = new ConsoleWatch(engine, _sp.GetRequiredService<IConfigBlobStore>(), _sp.GetService<NetworkOptimizer.Alerts.Events.IAlertEventBus>(),
            _sp.GetRequiredService<ILoggerFactory>().CreateLogger($"ChangeControlConsole[{slug}]"));
        var health = new SiteHealth.SiteHealthJob(slug, engine.Repositories,
            () => _sp.GetRequiredService<MonitoringInfluxRegistry>().GetFor(slug),
            () => _sp.GetRequiredService<Monitoring.IspHealth.IspHealthRegistry>().GetFor(slug, null),
            () => connection, _sp.GetService<NetworkOptimizer.Alerts.Events.IAlertEventBus>(),
            _sp.GetRequiredService<ILoggerFactory>().CreateLogger($"SiteHealth[{slug}]"));
        _health[slug] = health;
        health.Scored += () => HealthScored?.Invoke(slug);
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
        _health.TryRemove(slug, out _);
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
