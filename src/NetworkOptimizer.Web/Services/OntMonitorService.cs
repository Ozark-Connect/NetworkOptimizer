using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Polls external ONT (Optical Network Terminal) devices on a timer.
/// Analogous to CellularModemService but for fiber optics monitoring.
/// Resolves the appropriate IOntProvider per configuration and caches results
/// in memory. One instance exists per site, owned by
/// <see cref="ModemMonitorRegistry"/>: configurations, stats, and alerts all
/// belong to that site, and status scrapes route through the site's agent
/// tunnel when its devices are reached that way. The registry flips
/// <see cref="Active"/> as sites are enabled and disabled.
/// </summary>
public class OntMonitorService : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICredentialProtectionService _credentialProtection;
    private readonly SiteTunnelRouting _tunnelRouting;
    private readonly MonitoringInfluxClient _influx;
    private readonly NetworkOptimizer.Web.Services.Monitoring.OntAlertEvaluator _alertEvaluator;
    private readonly ILogger<OntMonitorService> _logger;
    private readonly Dictionary<string, IOntProvider> _providers;
    private readonly ConcurrentDictionary<int, OntStats> _statsCache = new();
    private volatile bool _hasPrimedOnce;
    private readonly Timer _pollTimer;
    private bool _isPolling;
    private readonly string _siteSlug;

    /// <summary>
    /// Whether the timer-driven poll loop runs. The registry keeps the default
    /// site's instance always active and toggles non-default instances with
    /// their site's Enabled flag. Manual polls from the UI work regardless.
    /// </summary>
    public bool Active { get; set; }

    public OntMonitorService(
        IServiceScopeFactory scopeFactory,
        IEnumerable<IOntProvider> providers,
        ICredentialProtectionService credentialProtection,
        SiteTunnelRouting tunnelRouting,
        MonitoringInfluxRegistry influxRegistry,
        MonitoringAlertRegistry alertRegistry,
        ILogger<OntMonitorService> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _scopeFactory = scopeFactory;
        _credentialProtection = credentialProtection;
        _tunnelRouting = tunnelRouting;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
        Active = _siteSlug == SiteManagementService.DefaultSiteSlug;
        _influx = influxRegistry.GetFor(_siteSlug);
        _alertEvaluator = alertRegistry.GetFor(_siteSlug).Ont;
        _logger = logger;
        _providers = providers.ToDictionary(p => p.ProviderKey, StringComparer.OrdinalIgnoreCase);

        // Prime poll 5 s after startup so dashboard has data; then check every 60 s
        _pollTimer = new Timer(_ => _ = PollAllAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// Creates a DI scope pinned to this instance's site so scoped services
    /// (repositories, DbContext) hit this site's database.
    /// </summary>
    private IServiceScope CreateSiteScope()
    {
        var scope = _scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(_siteSlug);
        return scope;
    }

    /// <summary>
    /// Get cached stats for a specific ONT without triggering a poll.
    /// </summary>
    public OntStats? GetCachedStats(int ontId)
    {
        return _statsCache.TryGetValue(ontId, out var stats) ? stats : null;
    }

    /// <summary>
    /// Get all cached ONT stats.
    /// </summary>
    public IReadOnlyDictionary<int, OntStats> GetAllCachedStats()
    {
        return _statsCache;
    }

    /// <summary>
    /// Get all ONT configurations (for UI and chart endpoints).
    /// </summary>
    public async Task<List<OntConfiguration>> GetConfigsAsync()
    {
        using var scope = CreateSiteScope();
        var repository = scope.ServiceProvider.GetRequiredService<IOntRepository>();
        return await repository.GetOntConfigurationsAsync();
    }

    /// <summary>
    /// Manually poll a single ONT by ID (used by UI refresh button).
    /// </summary>
    public async Task<OntStats?> PollOntAsync(int ontId)
    {
        using var scope = CreateSiteScope();
        var repository = scope.ServiceProvider.GetRequiredService<IOntRepository>();

        var config = await repository.GetOntConfigurationAsync(ontId);
        if (config == null)
        {
            _logger.LogWarning("Cannot poll ONT {Id}: configuration not found", ontId);
            return null;
        }

        return await PollSingleAsync(config, repository);
    }

    /// <summary>
    /// Save an ONT configuration (encrypts password before persisting).
    /// </summary>
    public async Task SaveOntAsync(OntConfiguration config)
    {
        if (!string.IsNullOrEmpty(config.Password) && !_credentialProtection.IsEncrypted(config.Password))
        {
            config.Password = _credentialProtection.Encrypt(config.Password);
        }

        var isNew = config.Id == 0;

        using var scope = CreateSiteScope();
        var repository = scope.ServiceProvider.GetRequiredService<IOntRepository>();
        await repository.SaveOntConfigurationAsync(config);

        if (isNew)
            await AlertRuleAutoEnable.EnableBySourceAsync(scope, "ont", _logger);
    }

    /// <summary>
    /// Delete an ONT configuration and remove cached stats.
    /// </summary>
    public async Task DeleteOntAsync(int id)
    {
        using var scope = CreateSiteScope();
        var repository = scope.ServiceProvider.GetRequiredService<IOntRepository>();
        await repository.DeleteOntConfigurationAsync(id);
        _statsCache.TryRemove(id, out _);
    }

    /// <summary>
    /// Test connectivity to an ONT without persisting anything.
    /// Used by the Settings page Test button.
    /// </summary>
    public async Task<(bool Success, string Message)> ProbeAsync(OntConfiguration config)
    {
        var provider = ResolveProvider(config.Provider);
        if (provider == null)
            return (false, $"No provider registered for '{config.Provider}'");

        var context = await ToContextAsync(config);
        return await provider.TestConnectionAsync(context);
    }

    private async Task PollAllAsync()
    {
        if (!Active) return;
        // While an agent-routed site's tunnel is down, every poll fails and stamps a
        // misleading device error, so the Settings card reads "Error" for a device
        // that's actually fine and recovers as soon as the agent returns. Skip polling
        // until the agent is back (the last known state and any real error are kept).
        if (await _tunnelRouting.IsViaAgentAsync(_siteSlug) && !_tunnelRouting.IsAgentOnline(_siteSlug))
            return;
        if (_isPolling)
        {
            _logger.LogDebug("ONT PollAllAsync skipped - already polling");
            return;
        }

        try
        {
            _isPolling = true;
            var forceAll = !_hasPrimedOnce;
            _logger.LogDebug("ONT PollAllAsync starting (forceAll={ForceAll})", forceAll);

            using var scope = CreateSiteScope();
            var repository = scope.ServiceProvider.GetRequiredService<IOntRepository>();
            var configs = await repository.GetEnabledOntConfigurationsAsync();
            _logger.LogDebug("ONT PollAllAsync found {Count} enabled configs", configs.Count);

            foreach (var config in configs)
            {
                if (!forceAll && config.LastPolled.HasValue)
                {
                    var elapsed = DateTime.UtcNow - config.LastPolled.Value;
                    if (elapsed.TotalSeconds < config.PollingIntervalSeconds)
                        continue;
                }

                await PollSingleAsync(config, repository);
            }

            _hasPrimedOnce = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ONT polling timer");
        }
        finally
        {
            _isPolling = false;
        }
    }

    private async Task<OntStats?> PollSingleAsync(OntConfiguration config, IOntRepository repository)
    {
        var provider = ResolveProvider(config.Provider);
        if (provider == null)
        {
            await UpdateConfigErrorAsync(repository, config, $"No provider for '{config.Provider}'");
            return null;
        }

        var context = await ToContextAsync(config);

        try
        {
            var stats = await provider.PollAsync(context);
            if (stats != null)
            {
                // Update last polled timestamp
                config.LastPolled = DateTime.UtcNow;
                config.LastError = null;
                config.UpdatedAt = DateTime.UtcNow;
                await repository.SaveOntConfigurationAsync(config);

                // Cache stats
                _statsCache[config.Id] = stats;

                // Fire-and-forget write to InfluxDB
                WriteToInflux(config, stats);

                _ = _alertEvaluator.EvaluateAsync(
                    config.Id, config.Name,
                    stats.RxPowerDbm, stats.PonLinkStatus, stats.FecErrors);

                _logger.LogDebug("ONT {Name} polled successfully: Rx={Rx} dBm", config.Name, stats.RxPowerDbm);
                return stats;
            }
            else
            {
                await UpdateConfigErrorAsync(repository, config, "Poll returned no data");
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error polling ONT {Name} at {Host}", config.Name, config.Host);
            await UpdateConfigErrorAsync(repository, config, ex.Message);
            return null;
        }
    }

    private async Task UpdateConfigErrorAsync(IOntRepository repository, OntConfiguration config, string error)
    {
        try
        {
            config.LastError = error;
            config.UpdatedAt = DateTime.UtcNow;
            await repository.SaveOntConfigurationAsync(config);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update ONT config error for {Name}", config.Name);
        }
    }

    private void WriteToInflux(OntConfiguration config, OntStats stats)
    {
        try
        {
            _ = _influx.WriteOntAsync(
                ontId: config.Id.ToString(),
                ontName: config.Name,
                rxPowerDbm: stats.RxPowerDbm,
                txPowerDbm: stats.TxPowerDbm,
                temperatureC: stats.TemperatureC,
                voltageV: stats.VoltageV,
                biasMa: stats.BiasMa,
                fecErrors: stats.FecErrors,
                bipErrors: stats.BipErrors,
                ponType: stats.PonType,
                wavelength: stats.WaveLength,
                ponLinkStatus: stats.PonLinkStatus != PonLinkState.Unknown ? stats.PonLinkStatus.ToInfluxValue() : null,
                bwpSpeedMbps: stats.BwpSpeedMbps,
                sfpLinkSpeedMbps: stats.SfpLinkSpeedMbps,
                timestamp: stats.Timestamp,
                linkUptimeSeconds: stats.LinkUptimeSeconds,
                oltVendor: stats.OltVendor,
                oltModel: stats.OltModel);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to write ONT stats to InfluxDB for {Name}", config.Name);
        }
    }

    private IOntProvider? ResolveProvider(string providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
            providerKey = "att-gateway";

        if (_providers.TryGetValue(providerKey, out var provider))
            return provider;

        _logger.LogError("No ONT provider registered for key '{Key}'", providerKey);
        return null;
    }

    private async Task<OntPollContext> ToContextAsync(OntConfiguration config)
    {
        string? password = null;
        if (!string.IsNullOrEmpty(config.Password))
        {
            try { password = _credentialProtection.Decrypt(config.Password); }
            catch { password = config.Password; }
        }

        // HTTP and SSH scrapes alike reach agent sites through the tunnel proxy
        // (raw TCP by host:port), so remote ONTs need no VPN routing.
        var (host, port) = await _tunnelRouting.RouteAsync(_siteSlug, config.Host, config.Port);

        return new OntPollContext
        {
            Id = config.Id,
            Name = config.Name,
            Host = host,
            ConfiguredHost = config.Host,
            Port = port,
            Username = string.IsNullOrEmpty(config.Username) ? null : config.Username,
            Password = password,
            PrivateKeyPath = string.IsNullOrEmpty(config.PrivateKeyPath) ? null : config.PrivateKeyPath,
        };
    }

    /// <summary>
    /// No-op. Owned by ModemMonitorRegistry but scope-forwarded, so the DI
    /// container calls Dispose at request/circuit scope end. Only the registry
    /// tears it down, via DisposeOwned. Mirrors UniFiConnectionService.
    /// </summary>
    public void Dispose() { }

    /// <summary>Real teardown, invoked only by the owning registry.</summary>
    internal void DisposeOwned()
    {
        _pollTimer.Dispose();
    }
}
