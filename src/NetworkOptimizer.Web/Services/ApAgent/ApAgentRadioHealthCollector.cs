using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Persists per-radio CCA and reset counters for one site, and evaluates them for the wedge.
///
/// SQLite rather than InfluxDB, deliberately: no existing measurement is per-radio, and adding a
/// radio tag to device_health would change its series key, which the additive-only schema rule
/// forbids. The counters here are a bounded, low-rate series that a wedge is trended against, not
/// something the time-series path was ever going to serve.
/// </summary>
public sealed class ApAgentRadioHealthCollector
{
    /// <summary>How long counter windows are kept. A wedge builds over hours, not weeks.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(6);

    private readonly IServiceProvider _serviceProvider;
    private readonly MonitoringAlertRegistry _alertRegistry;
    private readonly ILogger<ApAgentRadioHealthCollector> _logger;
    private readonly string _siteSlug;

    private readonly ConcurrentDictionary<string, ApAgentRadioHealthTracker> _trackers = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastPrunedAt = DateTime.MinValue;

    /// <summary>Creates the collector for one site.</summary>
    public ApAgentRadioHealthCollector(
        IServiceProvider serviceProvider,
        MonitoringAlertRegistry alertRegistry,
        ILogger<ApAgentRadioHealthCollector> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _serviceProvider = serviceProvider;
        _alertRegistry = alertRegistry;
        _logger = logger;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
    }

    /// <summary>
    /// Records one access point's radio counters. The first reading for a radio only seeds the
    /// tracker: there is nothing to difference it against yet.
    /// </summary>
    public async Task RecordAsync(
        string apMac, string? apName, IReadOnlyList<ApAgentRadioAirtime> radios, CancellationToken ct = default)
    {
        if (radios.Count == 0) return;

        var tracker = _trackers.GetOrAdd(apMac, _ => new ApAgentRadioHealthTracker());
        var windows = tracker.Observe(radios);
        if (windows.Count == 0) return;

        try
        {
            using var scope = CreateSiteScope();
            var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();

            foreach (var window in windows)
            {
                db.ApRadioHealthSamples.Add(new ApRadioHealthSample
                {
                    ApMac = apMac,
                    Radio = window.Radio,
                    Band = window.Band,
                    Channel = window.Channel,
                    SampleAt = window.At,
                    WindowSeconds = window.WindowSeconds,
                    CycleDelta = window.CycleDelta,
                    RxClearDelta = window.RxClearDelta,
                    TxFrameDelta = window.TxFrameDelta,
                    PhyErrDelta = window.PhyErrDelta,
                    PdevResets = window.PdevResets,
                    PdevResetDelta = window.PdevResetDelta,
                    BusyRatio = window.BusyRatio,
                    Wedged = window.Wedged,
                });
            }

            await PruneAsync(db, ct);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AP Agent radio health could not be stored for {Ap} (site {Site})", apMac, _siteSlug);
        }

        await _alertRegistry.GetFor(_siteSlug).RadioHealth.EvaluateAsync(apMac, apName, windows, ct);
    }

    private async Task PruneAsync(NetworkOptimizerDbContext db, CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastPrunedAt < PruneInterval) return;
        _lastPrunedAt = DateTime.UtcNow;

        var cutoff = DateTime.UtcNow - Retention;
        await db.ApRadioHealthSamples.Where(s => s.SampleAt < cutoff).ExecuteDeleteAsync(ct);
    }

    private IServiceScope CreateSiteScope()
    {
        var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(_siteSlug);
        return scope;
    }
}
