using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.Monitoring;

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
public sealed class StarlinkMonitorService : IStarlinkMonitorService, IDisposable
{
    /// <summary>How often the obstruction sky map is refreshed; it changes slowly and is a ~60 KB payload.</summary>
    private static readonly TimeSpan ObstructionMapRefresh = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How far back the alerting baselines look. Seven days is long enough that a re-aim or a
    /// cable change is followed rather than flagged, and short enough that the median still
    /// describes how the dish is behaving now.
    /// </summary>
    private static readonly TimeSpan BaselineWindow = TimeSpan.FromDays(7);

    /// <summary>How often the baselines are recomputed. They move over days, so this is a cheap once-per-poll-cycle read at worst.</summary>
    private static readonly TimeSpan BaselineRefresh = TimeSpan.FromHours(6);

    /// <summary>
    /// Aggregation the baseline query asks Influx for. Fifteen minutes gives ~670 points over the
    /// window, plenty for a stable median without pulling every raw sample across the wire.
    /// </summary>
    private static readonly TimeSpan BaselineAggregate = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Alignment samples needed in the window before its median is worth comparing against. A
    /// fresh install has none, and the drift alert stays disabled rather than baselining off three
    /// readings taken while the dish was still settling.
    /// </summary>
    private const int MinBaselineSamples = 24;

    /// <summary>How long a resolved WAN binding is reused. It only changes when someone renames a WAN or adds a dish.</summary>
    private static readonly TimeSpan WanBindingTtl = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SiteTunnelRouting _tunnelRouting;
    private readonly MonitoringInfluxClient _influx;
    private readonly StarlinkAlertEvaluator _alertEvaluator;
    private readonly ILogger<StarlinkMonitorService> _logger;
    private readonly Dictionary<string, IStarlinkProvider> _providers;
    private readonly Timer _pollingTimer;
    private readonly string _siteSlug;

    private readonly ConcurrentDictionary<int, StarlinkStats> _statsCache = new();
    private readonly ConcurrentDictionary<int, StarlinkObstructionMap> _obstructionMapCache = new();
    private readonly ConcurrentDictionary<int, DishBaseline> _baselines = new();
    private volatile bool _hasPrimedOnce;

    private string? _wanLabel;
    private DateTime _wanLabelLoadedAt = DateTime.MinValue;

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
        MonitoringAlertRegistry alertRegistry,
        ILogger<StarlinkMonitorService> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _scopeFactory = scopeFactory;
        _tunnelRouting = tunnelRouting;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
        Active = _siteSlug == SiteManagementService.DefaultSiteSlug;
        _influx = influxRegistry.GetFor(_siteSlug);
        _alertEvaluator = alertRegistry.GetFor(_siteSlug).Starlink;
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
    public Task<StarlinkStats?> GetCachedStatsAsync(int id)
    {
        return Task.FromResult(_statsCache.TryGetValue(id, out var stats) ? stats : null);
    }

    /// <summary>
    /// Get all cached terminal stats.
    /// </summary>
    public Task<IReadOnlyDictionary<int, StarlinkStats>> GetAllCachedStatsAsync()
    {
        return Task.FromResult<IReadOnlyDictionary<int, StarlinkStats>>(_statsCache);
    }

    /// <summary>
    /// Get the cached obstruction sky map for a terminal, if one has been fetched.
    /// </summary>
    public Task<StarlinkObstructionMap?> GetCachedObstructionMapAsync(int id)
    {
        return Task.FromResult(_obstructionMapCache.TryGetValue(id, out var map) ? map : null);
    }

    /// <summary>
    /// Manually trigger a poll for a specific terminal.
    /// </summary>
    public async Task<(bool success, string message)> PollStarlinkAsync(int id)
    {
        var config = await GetConfigAsync(id);
        if (config == null)
        {
            _logger.LogWarning("PollStarlinkAsync called for unknown Starlink config {Id}", id);
            return (false, "That terminal is no longer configured.");
        }

        await PollSingleAsync(config);

        // Read back rather than plumbing the reason out of the poll: the poll has just written
        // it to LastError, and the timer loop that shares this path wants no return value.
        var after = await GetConfigAsync(id);
        return string.IsNullOrEmpty(after?.LastError)
            ? (true, "Polled successfully.")
            : (false, after!.LastError!);
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

        // Adding or disabling a dish changes whether the WAN binding is unambiguous, so the
        // cached answer is dropped rather than left to age out and label a second dish with the
        // first one's WAN.
        InvalidateWanBinding();

        if (isNew)
            await AlertRuleAutoEnable.EnableBySourceAsync(scope, "starlink", _logger);
    }

    /// <summary>
    /// Enable or disable polling for one terminal (the Settings row Disable/Enable toggle).
    /// Disabled configs are skipped by the poll loop (GetEnabledStarlinkConfigurationsAsync)
    /// while their configuration is retained.
    /// </summary>
    public async Task SetStarlinkEnabledAsync(int id, bool enabled)
    {
        using var scope = CreateSiteScope();
        var repo = scope.ServiceProvider.GetRequiredService<IStarlinkRepository>();
        await repo.SetStarlinkEnabledAsync(id, enabled);

        InvalidateWanBinding();
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
        _baselines.TryRemove(id, out _);
        InvalidateWanBinding();
    }

    /// <summary>Forces the next poll to re-resolve which WAN the dish sits behind.</summary>
    private void InvalidateWanBinding() => _wanLabelLoadedAt = DateTime.MinValue;

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
            var result = await provider.PollAsync(context);
            var stats = result.Stats;

            if (stats != null)
            {
                // Persist the poll result BEFORE caching/Influx: the guarded write returns
                // false when the config was disabled mid-poll, so a paused terminal neither
                // caches nor charts.
                if (await UpdateConfigSuccessAsync(config.Id))
                {
                    _statsCache[config.Id] = stats;
                    WriteToInflux(config, stats);
                    await EvaluateAlertsAsync(config, stats);
                    await RefreshObstructionMapAsync(provider, context, config.Id);
                }
            }
            else
            {
                await UpdateConfigErrorAsync(
                    config.Id, result.FailureReason ?? "The terminal returned no data.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error polling Starlink terminal {Name} ({Id})", config.Name, config.Id);
            await UpdateConfigErrorAsync(config.Id, HttpFailureSummary.Describe(ex, config.Host));
        }
    }

    /// <summary>
    /// Hands this poll to the alert evaluator along with the two things it cannot derive from a
    /// single reading: the dish's own long-run baselines, and which WAN it serves. Failures here
    /// are logged and swallowed - alerting must never cost a poll its stats, its chart point, or
    /// its sky map.
    /// </summary>
    private async Task EvaluateAlertsAsync(StarlinkConfiguration config, StarlinkStats stats)
    {
        try
        {
            var baseline = await GetBaselineAsync(config.Id);
            await _alertEvaluator.EvaluateAsync(
                config.Id,
                config.Name,
                stats,
                ComputeAlignmentOffsetDeg(stats),
                baseline.AlignmentMedianDeg,
                baseline.EthCapableMbps,
                await ResolveWanLabelAsync());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Starlink alert evaluation failed for {Name} ({Id})", config.Name, config.Id);
        }
    }

    /// <summary>
    /// The dish's own long-run behavior, from the series already in Influx: the median boresight
    /// offset it normally sits at, and the fastest Ethernet speed it has been seen to negotiate.
    /// Both are self-calibrating by design - a hand-aimed fixed dish is several degrees off ideal
    /// from day one and works perfectly there, so drift is judged against where this dish sits
    /// rather than against where one ideally would.
    ///
    /// <para>
    /// Reads the LONGTERM bucket, which is where <c>QueryStarlinkAsync</c> looks. An install with
    /// no Influx, or a dish with too little history, comes back empty and simply leaves the two
    /// rules that need a baseline disabled.
    /// </para>
    /// </summary>
    private async Task<DishBaseline> GetBaselineAsync(int configId)
    {
        if (_baselines.TryGetValue(configId, out var cached) &&
            DateTime.UtcNow - cached.ComputedAt < BaselineRefresh)
        {
            return cached;
        }

        var to = DateTime.UtcNow;
        var series = await _influx.QueryStarlinkAsync(
            to - BaselineWindow, to, configId.ToString(), BaselineAggregate);
        var points = series.Values.FirstOrDefault() ?? new List<MonitoringInfluxClient.StarlinkPoint>();

        var offsets = points
            .Where(p => p.AlignmentOffsetDeg.HasValue)
            .Select(p => p.AlignmentOffsetDeg!.Value)
            .OrderBy(v => v)
            .ToList();
        double? median = offsets.Count >= MinBaselineSamples
            ? offsets.Count % 2 == 1
                ? offsets[offsets.Count / 2]
                : (offsets[offsets.Count / 2 - 1] + offsets[offsets.Count / 2]) / 2.0
            : null;

        // The maximum is the right statistic: eth_speed_mbps is the NEGOTIATED rate, so it cannot
        // read higher than the link actually reached, and a dish that has ever done 1000 is
        // 1000-capable. A genuine permanent downgrade (the dish moved onto a 100 Mbps segment for
        // good) alerts until the old speed ages out of the window, then stops on its own.
        var speeds = points.Where(p => p.EthSpeedMbps > 0).Select(p => p.EthSpeedMbps!.Value).ToList();
        int? capable = speeds.Count > 0 ? speeds.Max() : null;

        var baseline = new DishBaseline(median, capable, to);
        _baselines[configId] = baseline;

        // Says which of the two baseline-dependent rules are armed and why. A null median here is
        // the difference between "alignment drift is watching" and "alignment drift is off", and
        // without this line the two look identical from outside.
        _logger.LogDebug(
            "Starlink {Id} baseline over {Days}d: alignment={Median} from {Offsets} points, " +
            "capable={Capable} Mbps from {Speeds} points",
            configId, BaselineWindow.TotalDays,
            median?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "none",
            offsets.Count, capable?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none",
            speeds.Count);

        return baseline;
    }

    /// <summary>
    /// Best-effort binding of the dish to a WAN, so its alerts carry the same label everything
    /// else uses for that connection. Binds only when the answer is unambiguous: exactly one WAN
    /// that <see cref="StarlinkWanDetector"/> recognizes, and exactly one dish configured to sit
    /// behind it. With two dishes, or two Starlink WANs, nothing in the data says which serves
    /// which, and a confidently wrong WAN name on an alert is worse than none - the alerts then
    /// name the dish instead and fire regardless.
    /// </summary>
    private async Task<string?> ResolveWanLabelAsync()
    {
        if (DateTime.UtcNow - _wanLabelLoadedAt < WanBindingTtl) return _wanLabel;

        try
        {
            using var scope = CreateSiteScope();
            var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();

            var dishCount = await db.StarlinkConfigurations.CountAsync(c => c.Enabled);
            var matches = dishCount == 1
                ? await db.WanProfiles.AsNoTracking()
                    .Select(p => new { p.WanNetworkgroup, p.Name })
                    .ToListAsync()
                : [];

            var starlinkWans = matches
                .Where(p => StarlinkWanDetector.IsStarlinkWan(p.Name))
                .ToList();

            _wanLabel = starlinkWans.Count == 1
                ? GatewayWanHelper.FormatWanLabel(
                    starlinkWans[0].Name,
                    GatewayWanHelper.WanIndexFromKey(
                        GatewayWanHelper.WanInterfaceKeyFromKey(starlinkWans[0].WanNetworkgroup)),
                    null, null)
                : null;
        }
        catch (Exception ex)
        {
            // Keep whatever was resolved last: a failed lookup should cost the label, not the alert.
            _logger.LogDebug(ex, "Could not resolve the Starlink WAN binding for site {Site}", _siteSlug);
        }

        _wanLabelLoadedAt = DateTime.UtcNow;
        return _wanLabel;
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
            SiteSlug = _siteSlug,
            Name = config.Name,
            Host = host,
            ConfiguredHost = config.Host,
            Port = port,
        };
    }

    /// <summary>
    /// Guarded success write - returns false (and persists nothing) when the config was
    /// disabled while the poll was in flight, so callers can skip caching/Influx too.
    /// </summary>
    private async Task<bool> UpdateConfigSuccessAsync(int id)
    {
        try
        {
            using var scope = CreateSiteScope();
            var repo = scope.ServiceProvider.GetRequiredService<IStarlinkRepository>();
            return await repo.UpdateStarlinkPollResultAsync(id, DateTime.UtcNow, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update Starlink config {Id} after successful poll", id);
            return false;
        }
    }

    private async Task UpdateConfigErrorAsync(int id, string error)
    {
        try
        {
            using var scope = CreateSiteScope();
            var repo = scope.ServiceProvider.GetRequiredService<IStarlinkRepository>();
            await repo.UpdateStarlinkPollResultAsync(
                id, lastPolled: null, error.Length > 1000 ? error[..1000] : error);
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

    /// <summary>
    /// One dish's long-run behavior, as the alert rules that cannot judge from a single reading
    /// need it. Null members mean "not enough history", which disables the rule that reads them.
    /// </summary>
    /// <param name="AlignmentMedianDeg">Median boresight offset over the baseline window, degrees.</param>
    /// <param name="EthCapableMbps">Fastest Ethernet speed seen over the baseline window, Mbps.</param>
    /// <param name="ComputedAt">When this was computed, for the refresh interval.</param>
    private sealed record DishBaseline(double? AlignmentMedianDeg, int? EthCapableMbps, DateTime ComputedAt);
}
