using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>What one radio last reported, reduced to the counters that have a home today.</summary>
/// <param name="Radio">Interface name.</param>
/// <param name="Band">Band token as the agent reported it.</param>
/// <param name="Channel">Operating channel.</param>
/// <param name="NoiseFloor">Measured noise floor in dBm.</param>
/// <param name="Counters">Cumulative airtime and wedge counters.</param>
/// <param name="Deltas">The same counters' movement over the agent's own window.</param>
/// <param name="DeltaSeconds">Seconds the deltas span.</param>
/// <param name="At">When the server read them.</param>
public sealed record ApAgentRadioAirtime(
    string Radio,
    string? Band,
    int Channel,
    int? NoiseFloor,
    IReadOnlyDictionary<string, long> Counters,
    IReadOnlyDictionary<string, long> Deltas,
    double DeltaSeconds,
    DateTime At);

/// <summary>
/// Reads Wi-Fi telemetry from one site's AP Agents and writes it to the <c>wifi_client</c>
/// measurement, replacing the console's stat/sta data for the access points it reaches.
///
/// Sampling and writing are deliberately different rates: the AP measures far faster than the tier
/// writes, so samples fold into one point per client per write window rather than multiplying the
/// write volume on a measurement whose per-client queries are already expensive.
///
/// Driven by the monitoring agent's tier loop, which already honors this site's licensing and
/// monitoring-enabled gates, so nothing here re-checks them.
/// </summary>
public sealed class ApAgentTelemetryCollector
{
    /// <summary>Matches the console wifi tier's cadence, so both sources write at the same rate.</summary>
    public static readonly TimeSpan WriteWindow = TimeSpan.FromSeconds(30);

    /// <summary>An access point is a small target; a slow one must not hold up the pass.</summary>
    private static readonly TimeSpan ClientsTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RadiosTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Ceiling on one pass, so a site full of unresponsive access points cannot stall the tier.</summary>
    private static readonly TimeSpan PassBudget = TimeSpan.FromSeconds(25);

    /// <summary>The console device list changes rarely; re-reading it every pass would not.</summary>
    private static readonly TimeSpan DeviceCacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How stale the AP's own collection may be before its telemetry is refused. An agent that
    /// answers with tiers that stopped running is wedged, and its access point belongs back on the
    /// console path.
    /// </summary>
    private static readonly TimeSpan TierStaleAfter = TimeSpan.FromSeconds(180);

    /// <summary>Access points are polled a few at a time rather than all at once.</summary>
    private readonly SemaphoreSlim _pollGate = new(4);

    /// <summary>
    /// The radio counters W7 keeps: the airtime figures Channel Recommendation will consume, and
    /// the set the CCA wedge is read from. Everything else in the roughly 80 KB /radios reply is
    /// dropped on parse, because storing it wholesale has no home.
    /// </summary>
    private static readonly HashSet<string> RetainedRadioCounters = new(StringComparer.OrdinalIgnoreCase)
    {
        "cu_total", "cu_interf", "cu_self_tx", "cu_self_rx",
        "pdev_resets", "cycle_cnt", "rx_clear_cnt", "tx_frame_cnt", "phy_err_cnt",
    };

    private readonly IServiceProvider _serviceProvider;
    private readonly ApAgentTelemetryClient _telemetry;
    private readonly MonitoringInfluxClient _influx;
    private readonly ICredentialProtectionService _credentialProtection;
    private readonly ILogger<ApAgentTelemetryCollector> _logger;
    private readonly string _siteSlug;

    private readonly ApAgentCoverageLedger _coverage = new();
    private readonly ConcurrentDictionary<string, ApAgentWifiAccumulator> _accumulators = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<ApAgentRadioAirtime>> _radios = new(StringComparer.OrdinalIgnoreCase);

    private List<AccessPointTarget> _cachedTargets = new();
    private DateTime _targetsLoadedAt = DateTime.MinValue;
    private DateTime _lastWriteAt = DateTime.MinValue;

    /// <summary>Creates the collector for one site.</summary>
    public ApAgentTelemetryCollector(
        IServiceProvider serviceProvider,
        ApAgentTelemetryClient telemetry,
        MonitoringInfluxRegistry influxRegistry,
        ICredentialProtectionService credentialProtection,
        ILogger<ApAgentTelemetryCollector> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _serviceProvider = serviceProvider;
        _telemetry = telemetry;
        _credentialProtection = credentialProtection;
        _logger = logger;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
        _influx = influxRegistry.GetFor(_siteSlug);
    }

    /// <summary>
    /// Whether this access point's clients are being written from its own AP Agent. The console
    /// wifi tier asks per access point, because a site can hold some with an agent and some
    /// without, and the ones without must keep their console-sourced data.
    /// </summary>
    public bool CoversAp(string apMac) => _coverage.Covers(apMac, DateTime.UtcNow);

    /// <summary>
    /// The latest airtime and wedge counters per access point. Held in memory only: long-term
    /// airtime aggregation into ApChannelOutcome is a separate work item, and no Influx measurement
    /// is per-radio, so inventing one here is exactly what the additive-only rule forbids.
    /// </summary>
    public IReadOnlyList<ApAgentRadioAirtime> RadioAirtime(string apMac)
        => _radios.TryGetValue(ApAgentWifiFieldMapper.NormalizeMac(apMac), out var radios)
            ? radios
            : Array.Empty<ApAgentRadioAirtime>();

    /// <summary>
    /// One sampling pass. Polls every access point whose agent answers, folds what came back, and
    /// writes once the window has elapsed.
    /// </summary>
    public async Task SampleAsync(CancellationToken ct = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(PassBudget);

        try
        {
            await SampleCoreAsync(budget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("AP Agent telemetry pass ran out of budget (site {Site})", _siteSlug);
        }
    }

    private async Task SampleCoreAsync(CancellationToken ct)
    {
        if (!await IsSiteEnabledAsync(ct))
        {
            _coverage.ReleaseAll();
            _accumulators.Clear();
            return;
        }

        var targets = await GetTargetsAsync(ct);
        if (targets.Count == 0)
        {
            _coverage.ReleaseAll();
            return;
        }

        _coverage.RetainOnly(targets.Select(t => t.Mac).ToHashSet(StringComparer.OrdinalIgnoreCase));

        var now = DateTime.UtcNow;
        var writing = now - _lastWriteAt >= WriteWindow;

        await Task.WhenAll(targets.Select(t => PollAsync(t, now, ct)));

        if (!writing) return;
        _lastWriteAt = now;

        if (!_influx.IsConfigured) await _influx.ReconfigureAsync(ct);
        foreach (var target in targets)
            WriteFolded(target.Mac, now);

        await CollectRadiosAsync(targets, ct);
    }

    private async Task PollAsync(AccessPointTarget target, DateTime now, CancellationToken ct)
    {
        await _pollGate.WaitAsync(ct);
        try
        {
            var payload = await _telemetry.GetClientsAsync(_siteSlug, target.Host, target.Token, ClientsTimeout, ct);
            if (payload == null || IsStale(payload, now))
            {
                // Absent, unhealthy, or wedged: release now so the console path resumes on its next
                // tick rather than this access point going dark.
                _coverage.Release(target.Mac);
                _accumulators.TryRemove(target.Mac, out _);
                return;
            }

            _coverage.Claim(target.Mac, now);
            var accumulator = _accumulators.GetOrAdd(target.Mac, _ => new ApAgentWifiAccumulator());

            lock (accumulator)
            {
                foreach (var client in payload.Clients)
                {
                    // One point per client, never one per link: the agent has already folded an MLO
                    // client's links onto its MLD MAC.
                    var sample = ApAgentWifiFieldMapper.ToSample(client, target.Mac);
                    if (sample != null) accumulator.Add(sample, now);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AP Agent telemetry poll failed for {Host} (site {Site})", target.Host, _siteSlug);
            _coverage.Release(target.Mac);
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private void WriteFolded(string apMac, DateTime now)
    {
        if (!_accumulators.TryGetValue(apMac, out var accumulator)) return;

        IReadOnlyList<ApAgentWifiFolded> folded;
        lock (accumulator) folded = accumulator.Flush(now);

        long tickOffset = 0;
        foreach (var entry in folded)
        {
            // Same gate as the console path: a client that moved no traffic writes no point, so
            // swapping the source does not change how many points a site produces.
            if ((entry.TxThroughputBps ?? 0) <= 0 && (entry.RxThroughputBps ?? 0) <= 0) continue;

            var s = entry.Sample;
            _ = _influx.WriteWifiClientAsync(
                apMac: s.ApMac,
                band: s.Band,
                clientMac: s.ClientMac,
                signalDbm: s.SignalDbm,
                noiseDbm: s.NoiseDbm,
                txRateKbps: s.TxRateKbps,
                rxRateKbps: s.RxRateKbps,
                channel: s.Channel,
                channelWidth: s.ChannelWidth,
                satisfaction: s.Satisfaction,
                rssi: s.Rssi,
                txBytes: s.TxBytes,
                rxBytes: s.RxBytes,
                txThroughputBps: entry.TxThroughputBps,
                rxThroughputBps: entry.RxThroughputBps,
                isMlo: s.IsMlo,
                timestamp: now.AddTicks(tickOffset++),
                txRetries: s.TxRetries,
                txAttempts: s.TxAttempts,
                txDropped: s.TxDropped,
                latencyAvgMs: s.LatencyAvgMs,
                latencyMaxMs: s.LatencyMaxMs,
                tcpStalls: s.TcpStalls,
                tcpLatAvgMs: s.TcpLatAvgMs,
                ccq: s.Ccq,
                nss: s.Nss);
        }
    }

    private async Task CollectRadiosAsync(IReadOnlyList<AccessPointTarget> targets, CancellationToken ct)
    {
        var covered = targets.Where(t => _coverage.Covers(t.Mac, DateTime.UtcNow)).ToList();
        _radios.Clear();

        foreach (var target in covered)
        {
            if (ct.IsCancellationRequested) return;

            var payload = await _telemetry.GetRadiosAsync(_siteSlug, target.Host, target.Token, RadiosTimeout, ct);
            if (payload == null) continue;

            var at = DateTime.UtcNow;
            var radios = payload.Radios
                .Select(r => new ApAgentRadioAirtime(
                    r.Name,
                    r.Band,
                    r.Channel,
                    r.NoiseFloor,
                    Retain(r.Counters),
                    Retain(r.Deltas),
                    r.DeltaSeconds,
                    at))
                .ToList();

            _radios[target.Mac] = radios;
        }
    }

    /// <summary>Keeps only the counters that have a home, so the rest of the reply is not retained.</summary>
    private static IReadOnlyDictionary<string, long> Retain(Dictionary<string, long>? counters)
    {
        if (counters == null || counters.Count == 0) return new Dictionary<string, long>();
        return counters
            .Where(kv => RetainedRadioCounters.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the AP's own collection has stopped. Both client tiers being stale means the agent
    /// is answering with data that is no longer being refreshed.
    /// </summary>
    private static bool IsStale(ApAgentClientsPayload payload, DateTime now)
    {
        var fast = payload.Sources?.Fast;
        var slow = payload.Sources?.Slow;
        return !IsFresh(fast, now) && !IsFresh(slow, now);
    }

    private static bool IsFresh(ApAgentTierInfo? tier, DateTime now)
        => tier is { Available: true, LastCollectedAt: { } at } && now - at.ToUniversalTime() <= TierStaleAfter;

    private async Task<bool> IsSiteEnabledAsync(CancellationToken ct)
    {
        try
        {
            using var scope = CreateSiteScope();
            var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
            var setting = await db.SystemSettings.FindAsync(
                new object[] { ApAgentDeploymentService.SiteEnabledSettingKey }, ct);
            return bool.TryParse(setting?.Value, out var enabled) && enabled;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AP Agent telemetry could not read the site setting (site {Site})", _siteSlug);
            return false;
        }
    }

    private async Task<IReadOnlyList<AccessPointTarget>> GetTargetsAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _targetsLoadedAt < DeviceCacheTtl) return _cachedTargets;

        try
        {
            var connection = _serviceProvider.GetRequiredService<SiteConnectionRegistry>().GetFor(_siteSlug);
            var devices = await connection.GetDiscoveredDevicesAsync(ct);

            using var scope = CreateSiteScope();
            var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
            var records = await db.ApAgentDeployments.AsNoTracking().ToListAsync(ct);
            var byMac = records.ToDictionary(r => r.DeviceMac, StringComparer.OrdinalIgnoreCase);

            var targets = new List<AccessPointTarget>();
            foreach (var device in devices)
            {
                if (device.Type != DeviceType.AccessPoint) continue;
                if (string.IsNullOrEmpty(device.DisplayIpAddress)) continue;
                if (device.State != 1) continue;

                var mac = ApAgentWifiFieldMapper.NormalizeMac(device.Mac);
                if (!byMac.TryGetValue(mac, out var record) || !record.Enabled) continue;

                targets.Add(new AccessPointTarget(mac, device.DisplayIpAddress, ResolveToken(record)));
            }

            _cachedTargets = targets;
            _targetsLoadedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AP Agent telemetry could not list access points (site {Site})", _siteSlug);
        }

        return _cachedTargets;
    }

    /// <summary>A token that will not decrypt is left absent; the agent refuses the request and the console path keeps the access point.</summary>
    private string? ResolveToken(ApAgentDeployment record)
    {
        if (string.IsNullOrEmpty(record.Token)) return null;
        try
        {
            return _credentialProtection.Decrypt(record.Token);
        }
        catch
        {
            return null;
        }
    }

    private IServiceScope CreateSiteScope()
    {
        var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(_siteSlug);
        return scope;
    }

    private readonly record struct AccessPointTarget(string Mac, string Host, string? Token);
}
