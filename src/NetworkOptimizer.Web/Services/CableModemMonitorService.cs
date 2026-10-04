using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.CableModemProviders.Uci;
using NetworkOptimizer.Web.Services.Monitoring;
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
public sealed class CableModemMonitorService : ICableModemService, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICredentialProtectionService _credentialProtection;
    private readonly SiteTunnelRouting _tunnelRouting;
    private readonly MonitoringInfluxClient _influx;
    private readonly NetworkOptimizer.Web.Services.Monitoring.CableModemAlertEvaluator _alertEvaluator;
    private readonly ILogger<CableModemMonitorService> _logger;
    private readonly UciInformService _uciInforms;
    private readonly Dictionary<string, ICableModemProvider> _providers;
    private readonly Timer _pollingTimer;
    private readonly string _siteSlug;

    private readonly ConcurrentDictionary<int, CableModemStats> _statsCache = new();
    private volatile bool _hasPrimedOnce;
    private readonly ConcurrentDictionary<int, long> _previousTotalCorrectables = new();
    private readonly ConcurrentDictionary<int, long> _previousTotalUncorrectables = new();

    // Raw lines of each modem's event log as last seen: an entry not in here is new. Seeded from
    // InfluxDB on the first poll after startup so a restart does not re-write the whole log.
    private readonly ConcurrentDictionary<int, HashSet<string>> _knownEventLines = new();

    /// <summary>Only entries logged this recently alert; older ones are history (first sight, or catch-up after a gap).</summary>
    private static readonly TimeSpan EventAlertWindow = TimeSpan.FromMinutes(15);

    /// <summary>How far back the startup seed reads; well past any modem's log ring.</summary>
    private static readonly TimeSpan EventSeedLookback = TimeSpan.FromDays(30);

    /// <summary>How often the poll loop looks for newly adopted UCIs to configure automatically.</summary>
    private static readonly TimeSpan UciDiscoveryInterval = TimeSpan.FromMinutes(5);
    private DateTime _lastUciDiscovery = DateTime.MinValue;

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
        UciInformService uciInforms,
        ILogger<CableModemMonitorService> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _uciInforms = uciInforms;
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
    public Task<CableModemStats?> GetCachedStatsAsync(int cmId)
    {
        return Task.FromResult(_statsCache.TryGetValue(cmId, out var stats) ? stats : null);
    }

    /// <summary>
    /// Get all cached cable modem stats.
    /// </summary>
    public Task<IReadOnlyDictionary<int, CableModemStats>> GetAllCachedStatsAsync()
    {
        return Task.FromResult<IReadOnlyDictionary<int, CableModemStats>>(_statsCache);
    }

    /// <summary>
    /// Manually trigger a poll for a specific cable modem.
    /// </summary>
    public async Task<(bool success, string message)> PollCmAsync(int cmId)
    {
        var config = await GetConfigAsync(cmId);
        if (config == null)
        {
            _logger.LogWarning("PollCmAsync called for unknown CM config {Id}", cmId);
            return (false, "That cable modem is no longer configured.");
        }

        await PollSingleAsync(config);

        // Read back rather than plumbing the reason out of the poll: the poll has just written
        // it to LastError, and the timer loop that shares this path wants no return value.
        var after = await GetConfigAsync(cmId);
        return string.IsNullOrEmpty(after?.LastError)
            ? (true, "Polled successfully.")
            : (false, after!.LastError!);
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

        // A UCI config (or one that just stopped being one) changes what the gateway agent captures.
        await _uciInforms.PushCaptureConfigToSiteAsync(_siteSlug);
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
        var existing = await repo.GetCmConfigurationAsync(id);
        await repo.DeleteCmConfigurationAsync(id);

        _statsCache.TryRemove(id, out _);
        _previousTotalCorrectables.TryRemove(id, out _);
        _previousTotalUncorrectables.TryRemove(id, out _);
        _knownEventLines.TryRemove(id, out _);
        _alertEvaluator.Forget(id);

        if (existing?.Provider == UciInformService.ProviderKey)
        {
            // Deleting a UCI config is the user saying "not this one": automatic creation must not
            // bring it back.
            await _uciInforms.DismissAutoCreateAsync(_siteSlug, existing.Host);
            await _uciInforms.PushCaptureConfigToSiteAsync(_siteSlug);
        }
    }

    /// <summary>
    /// Enable or disable polling for one cable modem (the Settings row Disable/Enable toggle).
    /// Disabled configs are skipped by the poll loop (GetEnabledCmConfigurationsAsync)
    /// while their configuration is retained.
    /// </summary>
    public async Task SetCmEnabledAsync(int id, bool enabled)
    {
        using var scope = CreateSiteScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICmRepository>();
        await repo.SetCmEnabledAsync(id, enabled);
        await _uciInforms.PushCaptureConfigToSiteAsync(_siteSlug);

        if (!enabled)
        {
            // Drop the correctable/uncorrectable running totals (as DeleteCmAsync does) so
            // the first poll after re-enabling starts a fresh delta. Otherwise the errors
            // accumulated by the modem while paused land in one poll as a huge spike and
            // can trip the uncorrectables alert the moment the CM is resumed.
            _previousTotalCorrectables.TryRemove(id, out _);
            _previousTotalUncorrectables.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Test connectivity to a cable modem using the configured provider.
    /// </summary>
    public async Task<(bool Success, string Message)> ProbeAsync(CmConfiguration config)
    {
        var provider = ResolveProvider(config.Provider);
        if (provider == null)
            return (false, $"No provider registered for '{config.Provider}'");

        var context = ToContext(config);
        return await provider.TestConnectionAsync(context);
    }

    /// <summary>The site's adopted UCIs for the Settings picker, and whether a gateway agent can capture them.</summary>
    public async Task<UciChoices> GetUciChoicesAsync() =>
        new(await _uciInforms.ListUcisAsync(_siteSlug), _uciInforms.HasCapableAgent(_siteSlug));

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
            await DiscoverUcisAsync(repo);
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

        var context = ToContext(config);

        try
        {
            var result = await provider.PollAsync(context);
            var stats = result.Stats;

            if (stats != null)
            {
                if (await UpdateConfigSuccessAsync(config.Id))
                {
                    _statsCache[config.Id] = stats;
                    WriteToInflux(config, stats);
                    await ProcessModemEventsAsync(config, stats);
                }
            }
            else
            {
                await UpdateConfigErrorAsync(
                    config.Id, result.FailureReason ?? "The modem returned no data.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error polling cable modem {Name} ({Id})", config.Name, config.Id);
            await UpdateConfigErrorAsync(config.Id, HttpFailureSummary.Describe(ex, config.Host));
        }
    }

    /// <summary>
    /// Creates a cable modem config for every adopted UCI that has none, so a UCI shows up on CM
    /// Stats without setup. A config the user deleted is never recreated (see
    /// <see cref="UciInformService.DismissAutoCreateAsync"/>), a disabled one stays disabled, and the
    /// name is set only here: a later rename wins.
    /// </summary>
    private async Task DiscoverUcisAsync(ICmRepository repo)
    {
        if (DateTime.UtcNow - _lastUciDiscovery < UciDiscoveryInterval) return;
        _lastUciDiscovery = DateTime.UtcNow;
        try
        {
            var existing = await repo.GetCmConfigurationsAsync();
            var hosts = existing.Where(c => c.Provider == UciInformService.ProviderKey).Select(c => c.Host);
            foreach (var (mac, name) in await _uciInforms.FindUnconfiguredUcisAsync(_siteSlug, hosts))
            {
                await SaveCmAsync(new CmConfiguration
                {
                    Name = name,
                    Provider = UciInformService.ProviderKey,
                    Host = mac,
                    Port = 0,
                    Username = "",
                    Enabled = true,
                    PollingIntervalSeconds = 60,
                });
                _logger.LogInformation("Added cable modem monitoring for UniFi Cable Internet {Name} ({Mac})", name, mac);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UniFi Cable Internet discovery failed");
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

    private CmPollContext ToContext(CmConfiguration config)
    {
        string? password = null;
        if (!string.IsNullOrEmpty(config.Password))
        {
            try { password = _credentialProtection.Decrypt(config.Password); }
            catch { password = config.Password; }
        }

        return new CmPollContext
        {
            Id = config.Id,
            SiteSlug = _siteSlug,
            Name = config.Name,
            Host = config.Host,
            Port = config.Port,
            Dialer = _tunnelRouting.DialerFor(_siteSlug),
            Username = config.Username,
            Password = password,
            StatusPagePath = config.StatusPagePath,
        };
    }

    private async Task<bool> UpdateConfigSuccessAsync(int id)
    {
        try
        {
            using var scope = CreateSiteScope();
            var repo = scope.ServiceProvider.GetRequiredService<ICmRepository>();
            return await repo.UpdateCmPollResultAsync(id, DateTime.UtcNow, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update CM config {Id} after successful poll", id);
            return false;
        }
    }

    private async Task<bool> UpdateConfigErrorAsync(int id, string error)
    {
        try
        {
            using var scope = CreateSiteScope();
            var repo = scope.ServiceProvider.GetRequiredService<ICmRepository>();
            return await repo.UpdateCmPollResultAsync(id, null, error.Length > 1000 ? error[..1000] : error);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update CM config {Id} after error", id);
            return false;
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
    /// Handles what a modem reports about itself beyond signal levels: new DOCSIS event log entries
    /// go to InfluxDB (marks on Cable Modem Signal History) and, when recent and recognised, raise
    /// alerts; a changed reinit reason or a restart raises the reinit alert. Providers that report
    /// none of it pass straight through.
    /// </summary>
    private async Task ProcessModemEventsAsync(CmConfiguration config, CableModemStats stats)
    {
        try
        {
            if (stats.DocsisState != null || stats.UptimeSeconds.HasValue)
                await _alertEvaluator.EvaluateReinitAsync(config.Id, config.Name, stats.DocsisState?.ReinitReason, stats.UptimeSeconds);

            if (stats.Events.Count == 0)
                return;

            var cmId = config.Id.ToString();
            var now = DateTime.UtcNow;
            if (!_knownEventLines.TryGetValue(config.Id, out var known))
            {
                known = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    var stored = await _influx.QueryCableModemLogEventsAsync(cmId, now - EventSeedLookback, now);
                    known.UnionWith(stored.Select(e => e.Raw));
                }
                catch (Exception ex)
                {
                    // Without the seed every entry reads as new; the alert window still keeps old
                    // ones from alerting, and InfluxDB overwrites a re-written point in place.
                    _logger.LogDebug(ex, "Could not seed known DOCSIS events for {Name}", config.Name);
                }
            }

            foreach (var entry in stats.Events)
            {
                // No trustworthy time (logged before the modem synced its clock): table only.
                if (entry.Time is not { } at || known.Contains(entry.Raw))
                    continue;

                await _influx.WriteCableModemLogEventAsync(cmId, config.Name, entry.Level, entry.Kind, entry.Text, entry.Raw, at);
                if (entry.Kind != null && now - at <= EventAlertWindow && at <= now.AddMinutes(5))
                    await _alertEvaluator.PublishDocsisEventAsync(config.Id, config.Name, entry);
            }

            // Once an entry scrolls out of the modem's log it never comes back, so the current log is
            // the whole of what needs remembering.
            _knownEventLines[config.Id] = stats.Events.Select(e => e.Raw).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error processing DOCSIS events for cable modem {Name}", config.Name);
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
