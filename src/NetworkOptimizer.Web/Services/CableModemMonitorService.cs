using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Polls configured cable modems on a timer, caches the latest stats, and
/// writes time-series data to InfluxDB. Mirrors the CellularModemService
/// pattern. One instance exists per site, owned by
/// <see cref="ModemMonitorRegistry"/>: configurations, stats, and alerts all
/// belong to that site, and status page scrapes route through the site's
/// agent tunnel when its devices are reached that way. The registry flips
/// <see cref="Active"/> as sites are enabled and disabled; only active
/// instances poll.
/// </summary>
public sealed class CableModemMonitorService : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICredentialProtectionService _credentialProtection;
    private readonly SiteTunnelRouting _tunnelRouting;
    private readonly MonitoringInfluxClient _influx;
    private readonly NetworkOptimizer.Web.Services.Monitoring.CableModemAlertEvaluator _alertEvaluator;
    private readonly ILogger<CableModemMonitorService> _logger;
    private readonly Dictionary<string, ICableModemProvider> _providers;
    private readonly Timer _pollingTimer;
    private readonly string _siteSlug;

    private readonly ConcurrentDictionary<int, CableModemStats> _statsCache = new();
    private volatile bool _hasPrimedOnce;
    private readonly ConcurrentDictionary<int, long> _previousTotalCorrectables = new();
    private readonly ConcurrentDictionary<int, long> _previousTotalUncorrectables = new();

    private bool _isPolling;

    /// <summary>
    /// Whether the timer-driven poll loop runs. The registry keeps the default
    /// site's instance always active and toggles non-default instances with
    /// their site's Enabled flag. Manual polls from the UI work regardless.
    /// </summary>
    public bool Active { get; set; }

    public CableModemMonitorService(
        IServiceScopeFactory scopeFactory,
        IEnumerable<ICableModemProvider> providers,
        ICredentialProtectionService credentialProtection,
        SiteTunnelRouting tunnelRouting,
        MonitoringInfluxRegistry influxRegistry,
        MonitoringAlertRegistry alertRegistry,
        ILogger<CableModemMonitorService> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _scopeFactory = scopeFactory;
        _credentialProtection = credentialProtection;
        _tunnelRouting = tunnelRouting;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
        Active = _siteSlug == SiteManagementService.DefaultSiteSlug;
        _influx = influxRegistry.GetFor(_siteSlug);
        _alertEvaluator = alertRegistry.GetFor(_siteSlug).CableModem;
        _logger = logger;
        _providers = providers.ToDictionary(p => p.ProviderKey, StringComparer.OrdinalIgnoreCase);

        // Prime poll 5 s after startup so dashboard has data; then check every 60 s
        _pollingTimer = new Timer(
            _ => _ = PollAllAsync(),
            null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(60));
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
    /// Get cached stats for a specific cable modem without polling.
    /// </summary>
    public CableModemStats? GetCachedStats(int cmId)
    {
        return _statsCache.TryGetValue(cmId, out var stats) ? stats : null;
    }

    /// <summary>
    /// Get all cached cable modem stats.
    /// </summary>
    public IReadOnlyDictionary<int, CableModemStats> GetAllCachedStats()
    {
        return _statsCache;
    }

    /// <summary>
    /// Manually trigger a poll for a specific cable modem.
    /// </summary>
    public async Task PollCmAsync(int cmId)
    {
        var config = await GetConfigAsync(cmId);
        if (config == null)
        {
            _logger.LogWarning("PollCmAsync called for unknown CM config {Id}", cmId);
            return;
        }

        await PollSingleAsync(config);
    }

    /// <summary>
    /// Save a cable modem configuration. Encrypts the password before persisting.
    /// </summary>
    public async Task SaveCmAsync(CmConfiguration config)
    {
        if (!string.IsNullOrEmpty(config.Password) && !_credentialProtection.IsEncrypted(config.Password))
        {
            config.Password = _credentialProtection.Encrypt(config.Password);
        }

        var isNew = config.Id == 0;

        using var scope = CreateSiteScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICmRepository>();
        await repo.SaveCmConfigurationAsync(config);

        if (isNew)
            await AlertRuleAutoEnable.EnableBySourceAsync(scope, "cable_modem", _logger);
    }

    /// <summary>
    /// Get all cable modem configurations (enabled and disabled).
    /// </summary>
    public async Task<List<CmConfiguration>> GetConfigsAsync()
    {
        using var scope = CreateSiteScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICmRepository>();
        return await repo.GetCmConfigurationsAsync();
    }

    /// <summary>
    /// Delete a cable modem configuration and clear its cached stats.
    /// </summary>
    public async Task DeleteCmAsync(int id)
    {
        using var scope = CreateSiteScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICmRepository>();
        await repo.DeleteCmConfigurationAsync(id);

        _statsCache.TryRemove(id, out _);
        _previousTotalCorrectables.TryRemove(id, out _);
        _previousTotalUncorrectables.TryRemove(id, out _);
    }

    /// <summary>
    /// Test connectivity to a cable modem using the configured provider.
    /// </summary>
    public async Task<(bool Success, string Message)> ProbeAsync(CmConfiguration config)
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
            _logger.LogDebug("CM PollAllAsync skipped - already polling");
            return;
        }

        try
        {
            _isPolling = true;
            var forceAll = !_hasPrimedOnce;
            _logger.LogDebug("CM PollAllAsync starting (forceAll={ForceAll})", forceAll);

            using var scope = CreateSiteScope();
            var repo = scope.ServiceProvider.GetRequiredService<ICmRepository>();
            var configs = await repo.GetEnabledCmConfigurationsAsync();
            _logger.LogDebug("CM PollAllAsync found {Count} enabled configs", configs.Count);

            foreach (var config in configs)
            {
                if (!forceAll && config.LastPolled.HasValue)
                {
                    var elapsed = DateTime.UtcNow - config.LastPolled.Value;
                    if (elapsed.TotalSeconds < config.PollingIntervalSeconds)
                        continue;
                }

                await PollSingleAsync(config);
            }

            _hasPrimedOnce = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in cable modem polling timer");
        }
        finally
        {
            _isPolling = false;
        }
    }

    private async Task PollSingleAsync(CmConfiguration config)
    {
        var provider = ResolveProvider(config.Provider);
        if (provider == null)
        {
            await UpdateConfigErrorAsync(config.Id, $"No provider registered for '{config.Provider}'");
            return;
        }

        var context = await ToContextAsync(config);

        try
        {
            var stats = await provider.PollAsync(context);

            if (stats != null)
            {
                _statsCache[config.Id] = stats;
                await UpdateConfigSuccessAsync(config.Id);
                WriteToInflux(config, stats);
            }
            else
            {
                await UpdateConfigErrorAsync(config.Id, "Poll returned no data");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error polling cable modem {Name} ({Id})", config.Name, config.Id);
            await UpdateConfigErrorAsync(config.Id, ex.Message);
        }
    }

    private ICableModemProvider? ResolveProvider(string providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            _logger.LogWarning("Cable modem configuration has empty provider key");
            return null;
        }

        if (_providers.TryGetValue(providerKey, out var provider))
            return provider;

        _logger.LogWarning("No cable modem provider registered for key '{Key}'", providerKey);
        return null;
    }

    private async Task<CmPollContext> ToContextAsync(CmConfiguration config)
    {
        string? password = null;
        if (!string.IsNullOrEmpty(config.Password))
        {
            try { password = _credentialProtection.Decrypt(config.Password); }
            catch { password = config.Password; }
        }

        // Status page scrapes reach agent sites through the tunnel proxy: the
        // provider's HTTP client dials a loopback endpoint that the agent
        // forwards to the modem inside the site's network.
        var (host, port) = await _tunnelRouting.RouteAsync(_siteSlug, config.Host, config.Port);

        return new CmPollContext
        {
            Id = config.Id,
            Name = config.Name,
            Host = host,
            ConfiguredHost = config.Host,
            Port = port,
            Username = config.Username,
            Password = password,
            StatusPagePath = config.StatusPagePath,
        };
    }

    private async Task UpdateConfigSuccessAsync(int id)
    {
        try
        {
            using var scope = CreateSiteScope();
            var repo = scope.ServiceProvider.GetRequiredService<ICmRepository>();
            var config = await repo.GetCmConfigurationAsync(id);
            if (config != null)
            {
                config.LastPolled = DateTime.UtcNow;
                config.LastError = null;
                config.UpdatedAt = DateTime.UtcNow;
                await repo.SaveCmConfigurationAsync(config);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update CM config {Id} after successful poll", id);
        }
    }

    private async Task UpdateConfigErrorAsync(int id, string error)
    {
        try
        {
            using var scope = CreateSiteScope();
            var repo = scope.ServiceProvider.GetRequiredService<ICmRepository>();
            var config = await repo.GetCmConfigurationAsync(id);
            if (config != null)
            {
                config.LastError = error.Length > 1000 ? error[..1000] : error;
                config.UpdatedAt = DateTime.UtcNow;
                await repo.SaveCmConfigurationAsync(config);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update CM config {Id} after error", id);
        }
    }

    private async Task<CmConfiguration?> GetConfigAsync(int id)
    {
        using var scope = CreateSiteScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICmRepository>();
        return await repo.GetCmConfigurationAsync(id);
    }

    /// <summary>
    /// Write cable modem metrics to InfluxDB. Computes correctable/uncorrectable deltas
    /// since last poll; negative delta (modem reset) is reported as 0.
    /// </summary>
    private void WriteToInflux(CmConfiguration config, CableModemStats stats)
    {
        try
        {
            var currentCorrectables = stats.TotalCorrectables;
            var currentUncorrectables = stats.TotalUncorrectables;

            // Compute deltas
            long deltaCorrectables = 0;
            long deltaUncorrectables = 0;

            if (_previousTotalCorrectables.TryGetValue(config.Id, out var prevCorrectables))
            {
                deltaCorrectables = currentCorrectables - prevCorrectables;
                if (deltaCorrectables < 0) deltaCorrectables = 0; // modem reset
            }

            if (_previousTotalUncorrectables.TryGetValue(config.Id, out var prevUncorrectables))
            {
                deltaUncorrectables = currentUncorrectables - prevUncorrectables;
                if (deltaUncorrectables < 0) deltaUncorrectables = 0; // modem reset
            }

            _previousTotalCorrectables[config.Id] = currentCorrectables;
            _previousTotalUncorrectables[config.Id] = currentUncorrectables;

            _ = _alertEvaluator.EvaluateAsync(
                config.Id, config.Name,
                stats.DownstreamSnrAvgDb, stats.DownstreamPowerAvgDbmv, stats.UpstreamPowerAvgDbmv,
                stats.LockedDsChannels, stats.LockedUsChannels,
                deltaUncorrectables);

            // Fire-and-forget write to InfluxDB
            _ = Task.Run(async () =>
            {
                try
                {
                    await _influx.WriteCableModemAsync(
                        cmId: config.Id.ToString(),
                        cmName: config.Name,
                        dsPowerAvgDbmv: stats.DownstreamPowerAvgDbmv,
                        dsSnrAvgDb: stats.DownstreamSnrAvgDb,
                        usPowerAvgDbmv: stats.UpstreamPowerAvgDbmv,
                        lockedDsChannels: stats.LockedDsChannels,
                        lockedUsChannels: stats.LockedUsChannels,
                        correctablesDelta: deltaCorrectables,
                        uncorrectablesDelta: deltaUncorrectables,
                        correctablesTotal: currentCorrectables,
                        uncorrectablesTotal: currentUncorrectables,
                        channelsWithUncorrectables: stats.ChannelsWithUncorrectables,
                        timestamp: stats.Timestamp);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to write cable modem stats to InfluxDB for {Name}", config.Name);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error computing InfluxDB write for cable modem {Name}", config.Name);
        }
    }

    /// <summary>
    /// No-op. Owned by ModemMonitorRegistry but scope-forwarded, so the DI
    /// container calls Dispose at request/circuit scope end; disposing the poll
    /// timer here would silently stop the shared monitor. Only the registry
    /// tears it down, via DisposeOwned. Mirrors UniFiConnectionService.
    /// </summary>
    public void Dispose() { }

    /// <summary>Real teardown, invoked only by the owning registry.</summary>
    internal void DisposeOwned()
    {
        _pollingTimer.Dispose();
    }
}
