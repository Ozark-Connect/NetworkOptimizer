using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Monitoring;

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
public class OntMonitorService : IOntMonitorService, IDisposable
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
    public Task<OntStats?> GetCachedStatsAsync(int ontId)
    {
        return Task.FromResult(_statsCache.TryGetValue(ontId, out var stats) ? stats : null);
    }

    /// <summary>
    /// Get all cached ONT stats.
    /// </summary>
    public Task<IReadOnlyDictionary<int, OntStats>> GetAllCachedStatsAsync()
    {
        return Task.FromResult<IReadOnlyDictionary<int, OntStats>>(_statsCache);
    }

    /// <summary>
    /// Get all ONT configurations, including those attached to an SFP module
    /// (for the Settings management list).
    /// </summary>
    public async Task<List<OntConfiguration>> GetConfigsAsync()
    {
        using var scope = CreateSiteScope();
        var repository = scope.ServiceProvider.GetRequiredService<IOntRepository>();
        return await repository.GetOntConfigurationsAsync();
    }

    /// <summary>
    /// Standalone ONT configurations only - excludes configs attached to an SFP
    /// module, which surface as PON data on that module (SFP Stats), not as their
    /// own ONT device. Drives the ONT Stats tab, the Dashboard ONT card, and the
    /// ONT chart endpoint so an attached config never appears as a standalone ONT.
    /// </summary>
    public async Task<List<OntConfiguration>> GetStandaloneConfigsAsync()
    {
        var configs = await GetConfigsAsync();
        return configs.Where(c => c.AttachedSfpId == null).ToList();
    }

    /// <summary>
    /// Manually poll a single ONT by ID (used by UI refresh button).
    /// </summary>
    public async Task<(OntStats? stats, string? failureReason)> PollOntAsync(int ontId)
    {
        using var scope = CreateSiteScope();
        var repository = scope.ServiceProvider.GetRequiredService<IOntRepository>();

        var config = await repository.GetOntConfigurationAsync(ontId);
        if (config == null)
        {
            _logger.LogWarning("Cannot poll ONT {Id}: configuration not found", ontId);
            return (null, "That ONT is no longer configured.");
        }

        var stats = await PollSingleAsync(config, repository, await ResolveThresholdsAsync(scope));
        if (stats != null) return (stats, null);

        // The poll has just written why to LastError; read it back rather than plumbing it out
        // of a path the timer loop also uses.
        var after = await repository.GetOntConfigurationAsync(ontId);
        return (null, string.IsNullOrEmpty(after?.LastError) ? "The ONT returned no data." : after!.LastError);
    }

    /// <summary>
    /// Resolves the effective PON optical thresholds from this site's MonitoringSettings,
    /// falling back to the built-in defaults. External ONTs share the PON thresholds with
    /// the gateway's SFP DDM path, so the same user overrides drive both.
    /// </summary>
    private static async Task<SfpDdmThresholds> ResolveThresholdsAsync(IServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
        var settings = await db.MonitoringSettings.AsNoTracking().FirstOrDefaultAsync();
        return settings != null ? SfpDdmThresholds.FromSettings(settings) : SfpDdmThresholds.Defaults;
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

        // Attaching augmented PON polling to an SFP ONT unlocks the BIP/HEC error alerts -
        // enable them immediately (any save that lands an attachment, new or edited), so an
        // existing-ONT user doesn't have to wait for the next startup's freshly-seeded pass.
        if (config.AttachedSfpId.HasValue)
            await AlertRuleAutoEnable.EnablePatternsAsync(scope, AugmentedOntAlertPatterns, _logger);
    }

    /// <summary>PON error alerts unlocked by augmented (SFP-attached) ONT polling.</summary>
    private static readonly string[] AugmentedOntAlertPatterns = { "ont.bip_errors", "ont.hec_errors" };

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
    /// Enable or disable polling for one ONT (the Settings row Disable/Enable toggle).
    /// Disabled configs are skipped by the poll loop (GetEnabledOntConfigurationsAsync)
    /// while their configuration is retained.
    /// </summary>
    public async Task SetOntEnabledAsync(int id, bool enabled)
    {
        using var scope = CreateSiteScope();
        var repository = scope.ServiceProvider.GetRequiredService<IOntRepository>();
        await repository.SetOntEnabledAsync(id, enabled);
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

            var thresholds = await ResolveThresholdsAsync(scope);

            foreach (var config in configs)
            {
                // Configs attached to a monitored SFP module are polled by that
                // site's gateway SFP collection cycle (MonitoringCollectionAgent),
                // which merges their PON stats into the module's sfp measurement.
                if (config.AttachedSfpId.HasValue)
                    continue;

                if (!forceAll && config.LastPolled.HasValue)
                {
                    var elapsed = DateTime.UtcNow - config.LastPolled.Value;
                    if (elapsed.TotalSeconds < config.PollingIntervalSeconds)
                        continue;
                }

                await PollSingleAsync(config, repository, thresholds);
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

    private async Task<OntStats?> PollSingleAsync(
        OntConfiguration config, IOntRepository repository, SfpDdmThresholds thresholds)
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
            var result = await provider.PollAsync(context);
            var stats = result.Stats;
            if (stats != null)
            {
                // Persist only the poll result (never Enabled). If the config was disabled
                // mid-poll this returns false, so a paused ONT neither records a fresh poll
                // nor keeps caching/alerting/charting.
                if (!await repository.UpdateOntPollResultAsync(config.Id, DateTime.UtcNow, null))
                    return stats;

                // Cache stats
                _statsCache[config.Id] = stats;

                // Fire-and-forget write to InfluxDB
                WriteToInflux(config, stats);

                // Most standalone providers report BIP but neither HEC nor the FEC-enable state,
                // leaving FEC as the codeword-error signal (fecEnabled null). A provider that
                // serves the full PON set answers both, and on a FEC-disabled link that is the
                // difference between evaluating a counter that cannot move and the one that does.
                bool? fecEnabled = stats.Pon?.DsFecEnabled.HasValue == true || stats.Pon?.UsFecEnabled.HasValue == true
                    ? stats.Pon.DsFecEnabled == 1 || stats.Pon.UsFecEnabled == 1
                    : null;
                _ = _alertEvaluator.EvaluateAsync(
                    config.Id, config.Name,
                    stats.RxPowerDbm, stats.PonLinkStatus, stats.FecErrors,
                    stats.TemperatureC, thresholds.PonRxPowerLowDbm, thresholds.PonTempHighC,
                    bipErrors: stats.BipErrors,
                    hecErrors: stats.Pon?.HecUncorrected,
                    fecEnabled: fecEnabled,
                    sourceUrl: NetworkOptimizer.Web.Services.Monitoring.MonitoringLinks.HardwareStats(
                        "ont", DateTime.UtcNow, $"&ont={config.Id}"));

                _logger.LogDebug("ONT {Name} polled successfully: Rx={Rx} dBm", config.Name, stats.RxPowerDbm);
                return stats;
            }
            else
            {
                await UpdateConfigErrorAsync(
                    repository, config, result.FailureReason ?? "The ONT returned no data.");
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error polling ONT {Name} at {Host}", config.Name, config.Host);
            await UpdateConfigErrorAsync(repository, config, HttpFailureSummary.Describe(ex, config.Host));
            return null;
        }
    }

    private async Task UpdateConfigErrorAsync(IOntRepository repository, OntConfiguration config, string error)
    {
        try
        {
            // lastPolled null: an error does not advance LastPolled (it tracks last success).
            // Skips silently if the config was disabled meanwhile, so a paused row stays clean.
            await repository.UpdateOntPollResultAsync(config.Id, null, error);
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
                oltModel: stats.OltModel,
                pon: stats.Pon);
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
