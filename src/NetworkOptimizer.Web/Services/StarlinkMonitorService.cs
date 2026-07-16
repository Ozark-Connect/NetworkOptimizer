using System.Collections.Concurrent;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Polls configured Starlink terminals on a timer, caches the latest stats and
/// obstruction sky map, and writes time-series data to InfluxDB. Mirrors the
/// CableModemMonitorService pattern. One instance exists per site, owned by
/// <see cref="ModemMonitorRegistry"/>: configurations and stats belong to that
/// site, and dish gRPC calls route through the site's agent tunnel when its
/// devices are reached that way. The registry flips <see cref="Active"/> as
/// sites are enabled and disabled; only active instances poll.
/// </summary>
public sealed class StarlinkMonitorService : IDisposable
{
    /// <summary>How often the obstruction sky map is refreshed; it changes slowly and is a ~60 KB payload.</summary>
    private static readonly TimeSpan ObstructionMapRefresh = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SiteTunnelRouting _tunnelRouting;
    private readonly MonitoringInfluxClient _influx;
    private readonly ILogger<StarlinkMonitorService> _logger;
    private readonly Dictionary<string, IStarlinkProvider> _providers;
    private readonly Timer _pollingTimer;
    private readonly string _siteSlug;

    private readonly ConcurrentDictionary<int, StarlinkStats> _statsCache = new();
    private readonly ConcurrentDictionary<int, StarlinkObstructionMap> _obstructionMapCache = new();
    private volatile bool _hasPrimedOnce;

    private bool _isPolling;

    /// <summary>
    /// Whether the timer-driven poll loop runs. The registry keeps the default
    /// site's instance always active and toggles non-default instances with
    /// their site's Enabled flag. Manual polls from the UI work regardless.
    /// </summary>
    public bool Active { get; set; }

    public StarlinkMonitorService(
        IServiceScopeFactory scopeFactory,
        IEnumerable<IStarlinkProvider> providers,
        SiteTunnelRouting tunnelRouting,
        MonitoringInfluxRegistry influxRegistry,
        ILogger<StarlinkMonitorService> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _scopeFactory = scopeFactory;
        _tunnelRouting = tunnelRouting;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
        Active = _siteSlug == SiteManagementService.DefaultSiteSlug;
        _influx = influxRegistry.GetFor(_siteSlug);
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
    /// Get cached stats for a specific terminal without polling.
    /// </summary>
    public StarlinkStats? GetCachedStats(int id)
    {
        return _statsCache.TryGetValue(id, out var stats) ? stats : null;
    }

    /// <summary>
    /// Get all cached terminal stats.
    /// </summary>
    public IReadOnlyDictionary<int, StarlinkStats> GetAllCachedStats()
    {
        return _statsCache;
    }

    /// <summary>
    /// Get the cached obstruction sky map for a terminal, if one has been fetched.
    /// </summary>
    public StarlinkObstructionMap? GetCachedObstructionMap(int id)
    {
        return _obstructionMapCache.TryGetValue(id, out var map) ? map : null;
    }

    /// <summary>
    /// Manually trigger a poll for a specific terminal.
    /// </summary>
    public async Task PollStarlinkAsync(int id)
    {
        var config = await GetConfigAsync(id);
        if (config == null)
        {
            _logger.LogWarning("PollStarlinkAsync called for unknown Starlink config {Id}", id);
            return;
        }

        await PollSingleAsync(config);
    }

    /// <summary>
    /// Save a Starlink terminal configuration.
    /// </summary>
    public async Task SaveStarlinkAsync(StarlinkConfiguration config)
    {
        var isNew = config.Id == 0;

        using var scope = CreateSiteScope();
        var repo = scope.ServiceProvider.GetRequiredService<IStarlinkRepository>();
        await repo.SaveStarlinkConfigurationAsync(config);

        if (isNew)
            await AlertRuleAutoEnable.EnableBySourceAsync(scope, "starlink", _logger);
    }

    /// <summary>
    /// Get all Starlink terminal configurations (enabled and disabled).
    /// </summary>
    public async Task<List<StarlinkConfiguration>> GetConfigsAsync()
    {
        using var scope = CreateSiteScope();
        var repo = scope.ServiceProvider.GetRequiredService<IStarlinkRepository>();
        return await repo.GetStarlinkConfigurationsAsync();
    }

    /// <summary>
    /// Delete a Starlink terminal configuration and clear its cached stats.
    /// </summary>
    public async Task DeleteStarlinkAsync(int id)
    {
        using var scope = CreateSiteScope();
        var repo = scope.ServiceProvider.GetRequiredService<IStarlinkRepository>();
        await repo.DeleteStarlinkConfigurationAsync(id);

        _statsCache.TryRemove(id, out _);
        _obstructionMapCache.TryRemove(id, out _);
    }

    /// <summary>
    /// Test connectivity to a terminal using the configured provider.
    /// </summary>
    public async Task<(bool Success, string Message)> ProbeAsync(StarlinkConfiguration config)
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
            _logger.LogDebug("Starlink PollAllAsync skipped - already polling");
            return;
        }

        try
        {
            _isPolling = true;
            var forceAll = !_hasPrimedOnce;
            _logger.LogDebug("Starlink PollAllAsync starting (forceAll={ForceAll})", forceAll);

            using var scope = CreateSiteScope();
            var repo = scope.ServiceProvider.GetRequiredService<IStarlinkRepository>();
            var configs = await repo.GetEnabledStarlinkConfigurationsAsync();
            _logger.LogDebug("Starlink PollAllAsync found {Count} enabled configs", configs.Count);

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
            _logger.LogError(ex, "Error in Starlink polling timer");
        }
        finally
        {
            _isPolling = false;
        }
    }

    private async Task PollSingleAsync(StarlinkConfiguration config)
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
                await RefreshObstructionMapAsync(provider, context, config.Id);
            }
            else
            {
                await UpdateConfigErrorAsync(config.Id, "Poll returned no data");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error polling Starlink terminal {Name} ({Id})", config.Name, config.Id);
            await UpdateConfigErrorAsync(config.Id, ex.Message);
        }
    }

    private async Task RefreshObstructionMapAsync(
        IStarlinkProvider provider, StarlinkPollContext context, int configId)
    {
        if (_obstructionMapCache.TryGetValue(configId, out var cached) &&
            DateTime.UtcNow - cached.Timestamp < ObstructionMapRefresh)
        {
            return;
        }

        var map = await provider.GetObstructionMapAsync(context);
        if (map != null)
            _obstructionMapCache[configId] = map;
    }

    private IStarlinkProvider? ResolveProvider(string providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            _logger.LogWarning("Starlink configuration has empty provider key");
            return null;
        }

        if (_providers.TryGetValue(providerKey, out var provider))
            return provider;

        _logger.LogWarning("No Starlink provider registered for key '{Key}'", providerKey);
        return null;
    }

    private async Task<StarlinkPollContext> ToContextAsync(StarlinkConfiguration config)
    {
        // gRPC to agent sites goes through the tunnel proxy: the channel dials
        // a loopback endpoint whose bytes the agent pumps to the dish inside
        // the site's network (the proxy is a raw TCP relay, so plaintext
        // HTTP/2 passes through unmodified).
        var (host, port) = await _tunnelRouting.RouteAsync(_siteSlug, config.Host, config.Port);

        return new StarlinkPollContext
        {
            Id = config.Id,
            Name = config.Name,
            Host = host,
            ConfiguredHost = config.Host,
            Port = port,
        };
    }

    private async Task UpdateConfigSuccessAsync(int id)
    {
        try
        {
            using var scope = CreateSiteScope();
            var repo = scope.ServiceProvider.GetRequiredService<IStarlinkRepository>();
            var config = await repo.GetStarlinkConfigurationAsync(id);
            if (config != null)
            {
                config.LastPolled = DateTime.UtcNow;
                config.LastError = null;
                config.UpdatedAt = DateTime.UtcNow;
                await repo.SaveStarlinkConfigurationAsync(config);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update Starlink config {Id} after successful poll", id);
        }
    }

    private async Task UpdateConfigErrorAsync(int id, string error)
    {
        try
        {
            using var scope = CreateSiteScope();
            var repo = scope.ServiceProvider.GetRequiredService<IStarlinkRepository>();
            var config = await repo.GetStarlinkConfigurationAsync(id);
            if (config != null)
            {
                config.LastError = error.Length > 1000 ? error[..1000] : error;
                config.UpdatedAt = DateTime.UtcNow;
                await repo.SaveStarlinkConfigurationAsync(config);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update Starlink config {Id} after error", id);
        }
    }

    private async Task<StarlinkConfiguration?> GetConfigAsync(int id)
    {
        using var scope = CreateSiteScope();
        var repo = scope.ServiceProvider.GetRequiredService<IStarlinkRepository>();
        return await repo.GetStarlinkConfigurationAsync(id);
    }

    /// <summary>
    /// Write Starlink terminal metrics to InfluxDB.
    /// </summary>
    private void WriteToInflux(StarlinkConfiguration config, StarlinkStats stats)
    {
        try
        {
            var alignmentOffset = ComputeAlignmentOffsetDeg(stats);

            // Fire-and-forget write to InfluxDB
            _ = Task.Run(async () =>
            {
                try
                {
                    await _influx.WriteStarlinkAsync(
                        starlinkId: config.Id.ToString(),
                        starlinkName: config.Name,
                        powerInW: stats.PowerInWatts,
                        powerInAvgW: stats.PowerInAvgWatts,
                        powerInMaxW: stats.PowerInMaxWatts,
                        pingDropRateAvg: stats.PingDropRateAvg,
                        pingDropRateMax: stats.PingDropRateMax,
                        fractionObstructed: stats.FractionObstructed,
                        currentlyObstructed: stats.CurrentlyObstructed,
                        ethSpeedMbps: stats.EthSpeedMbps,
                        uptimeS: stats.UptimeSeconds,
                        gpsSats: stats.GpsSatellites,
                        gpsValid: stats.GpsValid,
                        tiltAngleDeg: stats.TiltAngleDeg,
                        alignmentOffsetDeg: alignmentOffset,
                        attitudeUncertaintyDeg: stats.AttitudeUncertaintyDeg,
                        outageCountDelta: stats.OutageCountDelta,
                        outageSecondsDelta: stats.OutageSecondsDelta,
                        alertCount: stats.ActiveAlerts.Count,
                        alerts: stats.ActiveAlerts.Count > 0 ? string.Join(",", stats.ActiveAlerts) : null,
                        snrPersistentlyLow: stats.IsSnrPersistentlyLow,
                        softwareUpdateState: stats.SoftwareUpdateState,
                        disablementCode: stats.DisablementCode,
                        dlRestrictedReason: stats.DownlinkRestrictedReason,
                        ulRestrictedReason: stats.UplinkRestrictedReason,
                        hardwareSelfTest: stats.HardwareSelfTest,
                        classOfService: stats.ClassOfService,
                        mobilityClass: stats.MobilityClass,
                        timestamp: stats.Timestamp);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to write Starlink stats to InfluxDB for {Name}", config.Name);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error computing InfluxDB write for Starlink terminal {Name}", config.Name);
        }
    }

    /// <summary>
    /// Angular offset between the dish's actual and desired boresight, degrees.
    /// Azimuth error is scaled by cos(elevation) so it measures true sky angle
    /// rather than compass degrees (which inflate near zenith).
    /// </summary>
    internal static double? ComputeAlignmentOffsetDeg(StarlinkStats stats)
    {
        if (stats.BoresightAzimuthDeg is not double az ||
            stats.BoresightElevationDeg is not double el ||
            stats.DesiredBoresightAzimuthDeg is not double desiredAz ||
            stats.DesiredBoresightElevationDeg is not double desiredEl)
        {
            return null;
        }

        var dAz = ((az - desiredAz + 540) % 360) - 180;
        var dEl = el - desiredEl;
        var azSky = dAz * Math.Cos(el * Math.PI / 180.0);
        return Math.Sqrt(azSky * azSky + dEl * dEl);
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
