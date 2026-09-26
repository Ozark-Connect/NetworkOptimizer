using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// One site's module update state: whether its deployed Performance Tweaks boot scripts or WAN
/// Steering binary are older than the embedded copies. Shared by every circuit showing the site.
/// </summary>
public sealed class SiteModuleUpdateState
{
    /// <summary>Raised when the cached update state changes so consumers can re-render.</summary>
    public event Action? OnStateChanged;

    /// <summary>True when one or more deployed Performance Tweaks boot scripts are out of date.</summary>
    public bool PerfTweaksUpdateAvailable { get; private set; }

    /// <summary>True when the deployed WAN Steering binary is older than the embedded version.</summary>
    public bool WanSteerUpdateAvailable { get; private set; }

    /// <summary>
    /// Updates the Performance Tweaks state from a freshly fetched status. Callers should pass a
    /// successfully-read status (Error == null).
    /// </summary>
    public void NotifyPerfTweaksStatus(PerfTweaksStatus status) =>
        Set(status.Tweaks.Values.Any(t => t.ScriptOutdated), WanSteerUpdateAvailable);

    /// <summary>
    /// Updates the WAN Steering state from a freshly fetched status. Callers should pass a status
    /// whose binary was actually read (BinaryDeployed) so a transient SSH failure doesn't clear it.
    /// </summary>
    public void NotifyWanSteerStatus(WanSteerStatus status) =>
        Set(PerfTweaksUpdateAvailable, WanSteerDeploymentService.IsBinaryOutdated(status));

    internal void Set(bool perfTweaks, bool wanSteer)
    {
        if (perfTweaks == PerfTweaksUpdateAvailable && wanSteer == WanSteerUpdateAvailable)
            return;
        PerfTweaksUpdateAvailable = perfTweaks;
        WanSteerUpdateAvailable = wanSteer;
        OnStateChanged?.Invoke();
    }
}

/// <summary>
/// Owns each site's <see cref="SiteModuleUpdateState"/> and decides when to re-check it over
/// gateway SSH. A check runs on a site's first opportunity (Console connected, agent tunnel up),
/// then only when something that can change the answer changes: the Console reconnects, the agent
/// tunnel comes back, the gateway's MAC or firmware changes, or the Gateway SSH settings change.
/// A daily re-check is the backstop. Page loads never trigger one.
/// </summary>
public class ModuleUpdateRegistry : BackgroundService, ISiteScopedRegistry
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromMinutes(15);

    private readonly IServiceProvider _serviceProvider;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly SiteConnectionRegistry _connections;
    private readonly GatewaySshRegistry _gatewaySsh;
    private readonly NetworkOptimizer.Core.ISiteWorkGate _siteWorkGate;
    private readonly ILogger<ModuleUpdateRegistry> _logger;
    private readonly ConcurrentDictionary<string, SiteModuleUpdateState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Tracker> _trackers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What a site's last tick saw, to tell a trigger from a steady state.</summary>
    private sealed class Tracker
    {
        public bool Dirty = true;
        public bool WasConnected;
        public bool WasAwaitingAgent;
        public string? GatewayFingerprint;
        public int? SshFingerprint;
        public DateTime LastSuccessUtc = DateTime.MinValue;
        public DateTime RetryAfterUtc = DateTime.MinValue;

        public void Trigger()
        {
            Dirty = true;
            RetryAfterUtc = DateTime.MinValue;
        }
    }

    public ModuleUpdateRegistry(
        IServiceProvider serviceProvider,
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        SiteConnectionRegistry connections,
        GatewaySshRegistry gatewaySsh,
        NetworkOptimizer.Core.ISiteWorkGate siteWorkGate,
        ILogger<ModuleUpdateRegistry> logger)
    {
        _serviceProvider = serviceProvider;
        _mainDbFactory = mainDbFactory;
        _connections = connections;
        _gatewaySsh = gatewaySsh;
        _siteWorkGate = siteWorkGate;
        _logger = logger;
    }

    /// <summary>The update state for a site, created on first use.</summary>
    public SiteModuleUpdateState GetFor(string slug) => _states.GetOrAdd(slug, _ => new SiteModuleUpdateState());

    /// <inheritdoc />
    public Func<ValueTask>? EvictSite(string slug)
    {
        _states.TryRemove(slug, out _);
        _trackers.TryRemove(slug, out _);
        return null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var slug in await GetOperationalSitesAsync(stoppingToken))
                {
                    try
                    {
                        await TickSiteAsync(slug, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogDebug(ex, "Module update check failed for site {Slug}", slug);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Module update pass failed");
            }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<List<string>> GetOperationalSitesAsync(CancellationToken ct)
    {
        var sites = new List<string>();
        if (_siteWorkGate.IsSiteOperational(SiteManagementService.DefaultSiteSlug))
            sites.Add(SiteManagementService.DefaultSiteSlug);

        await using var db = await _mainDbFactory.CreateDbContextAsync(ct);
        var setting = await db.SystemSettings.FindAsync(new object[] { SystemSettingKeys.MultiSiteEnabled }, ct);
        if (bool.TryParse(setting?.Value, out var enabled) && enabled)
        {
            var slugs = await db.Sites.AsNoTracking()
                .Where(s => s.Enabled && !s.IsDefault)
                .Select(s => s.Slug)
                .ToListAsync(ct);
            sites.AddRange(slugs.Where(_siteWorkGate.IsSiteOperational));
        }
        return sites;
    }

    private async Task TickSiteAsync(string slug, CancellationToken ct)
    {
        var t = _trackers.GetOrAdd(slug, _ => new Tracker());
        var connection = _connections.GetFor(slug);
        var ssh = _gatewaySsh.GetFor(slug);

        var connected = connection.IsConnected;
        if (connected && !t.WasConnected) t.Trigger();
        t.WasConnected = connected;
        if (!connected) return;

        var awaitingAgent = await ssh.IsAwaitingAgentTunnelAsync();
        if (t.WasAwaitingAgent && !awaitingAgent) t.Trigger();
        t.WasAwaitingAgent = awaitingAgent;
        if (awaitingAgent) return;

        var settings = await ssh.GetSettingsAsync();
        if (!settings.Enabled || string.IsNullOrEmpty(settings.Host) || !settings.HasCredentials) return;
        var sshFingerprint = HashCode.Combine(settings.Host, settings.Port, settings.Username,
            settings.Password, settings.PrivateKeyPath, settings.HasStoredKey);
        if (t.SshFingerprint is { } previousSsh && previousSsh != sshFingerprint) t.Trigger();
        t.SshFingerprint = sshFingerprint;

        // Cached devices only: this watcher must not add Console load. Unknown keeps the last value.
        var gateway = connection.CachedDevices?.FirstOrDefault(d => d.HardwareType == DeviceType.Gateway);
        if (gateway != null)
        {
            var gatewayFingerprint = $"{gateway.Mac}|{gateway.Firmware}";
            if (t.GatewayFingerprint != null && t.GatewayFingerprint != gatewayFingerprint) t.Trigger();
            t.GatewayFingerprint = gatewayFingerprint;
        }

        var now = DateTime.UtcNow;
        if (!t.Dirty && now - t.LastSuccessUtc < RecheckInterval) return;
        if (now < t.RetryAfterUtc) return;

        if (await CheckAsync(slug, ct))
        {
            t.Dirty = false;
            t.LastSuccessUtc = now;
        }
        else
        {
            t.RetryAfterUtc = now + FailureRetryInterval;
        }
    }

    /// <summary>Runs the Performance Tweaks and WAN Steering status reads for a site. False on failure.</summary>
    private async Task<bool> CheckAsync(string slug, CancellationToken ct)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(slug);
        // A background check with no user behind it, so the gated status reads run as system.
        using var systemScope = Identity.SystemScope.Enter(scope.ServiceProvider, "module-update-check");
        var perf = scope.ServiceProvider.GetRequiredService<IPerfTweaksDeploymentService>();
        var wan = scope.ServiceProvider.GetRequiredService<IWanSteerDeploymentService>();

        var perfStatus = await perf.CheckAllStatusAsync();
        if (perfStatus.Error != null)
        {
            _logger.LogDebug("Module update check for site {Slug} could not read gateway status: {Error}", slug, perfStatus.Error);
            return false;
        }

        ct.ThrowIfCancellationRequested();
        var wanStatus = await wan.GetStatusAsync();
        GetFor(slug).Set(
            perfStatus.Tweaks.Values.Any(x => x.ScriptOutdated),
            WanSteerDeploymentService.IsBinaryOutdated(wanStatus));
        return true;
    }
}
