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
/// <param name="Width">Operating channel width in MHz; 0 when the agent did not report one.</param>
/// <param name="NoiseFloor">Measured noise floor in dBm.</param>
/// <param name="Counters">Cumulative airtime and wedge counters.</param>
/// <param name="Deltas">The same counters' movement over the agent's own window.</param>
/// <param name="DeltaSeconds">Seconds the deltas span.</param>
/// <param name="At">When the server read them.</param>
public sealed record ApAgentRadioAirtime(
    string Radio,
    string? Band,
    int Channel,
    int Width,
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

    private readonly ApAgentTargetDirectory _directory;
    private readonly ApAgentTelemetryClient _telemetry;
    private readonly ApAgentInsightsRegistry.SiteApAgentInsights _insights;
    private readonly MonitoringInfluxClient _influx;
    private readonly MonitoringLiveStats _liveStats;
    private readonly ILogger<ApAgentTelemetryCollector> _logger;
    private readonly string _siteSlug;

    private readonly ApAgentCoverageLedger _coverage = new();
    private readonly ApAgentAirtimeAggregator _airtime = new();
    private readonly ConcurrentDictionary<string, ApAgentWifiAccumulator> _accumulators = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Byte counters as of the previous pass, so throughput resolves every poll rather than only
    /// when a write window closes. Separate from the accumulator's own tracker on purpose: that one
    /// measures across a window, this one across a pass, and sharing a baseline would corrupt both.
    /// </summary>
    private readonly ConcurrentDictionary<string, PassBytes> _passBytes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<ApAgentRadioAirtime>> _radios = new(StringComparer.OrdinalIgnoreCase);

    private DateTime _lastWriteAt = DateTime.MinValue;

    /// <summary>Creates the collector for one site.</summary>
    public ApAgentTelemetryCollector(
        ApAgentTargetDirectory directory,
        ApAgentTelemetryClient telemetry,
        MonitoringInfluxRegistry influxRegistry,
        ApAgentInsightsRegistry insights,
        MonitoringLiveStatsRegistry liveStats,
        ILogger<ApAgentTelemetryCollector> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _directory = directory;
        _telemetry = telemetry;
        _logger = logger;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
        _influx = influxRegistry.GetFor(_siteSlug);
        _insights = insights.GetFor(_siteSlug);
        _liveStats = liveStats.GetFor(_siteSlug);
    }

    /// <summary>
    /// Whether this access point's clients are being written from its own AP Agent. The console
    /// wifi tier asks per access point, because a site can hold some with an agent and some
    /// without, and the ones without must keep their console-sourced data.
    /// </summary>
    public bool CoversAp(string apMac) => _coverage.Covers(apMac, DateTime.UtcNow);

    /// <summary>
    /// The hourly airtime aggregates the channel memory sweep consumes. The aggregator holds them
    /// in memory only; persistence into ApChannelOutcome happens inside the sweep's own atomic
    /// commit, never here, so the two sources can never both write the same radio-hour.
    /// </summary>
    public ApAgentAirtimeAggregator Airtime => _airtime;

    /// <summary>
    /// The latest airtime and wedge counters per access point. Held in memory only: hourly
    /// aggregates flow to ApChannelOutcome through <see cref="Airtime"/>, and no Influx measurement
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
        if (!await _directory.IsSiteEnabledAsync(_siteSlug, ct))
        {
            _coverage.ReleaseAll();
            _accumulators.Clear();
            return;
        }

        var targets = await _directory.GetTargetsAsync(_siteSlug, ct);
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

        PrunePassBytes(now);

        if (!_influx.IsConfigured) await _influx.ReconfigureAsync(ct);
        foreach (var target in targets)
            WriteFolded(target.Mac, now);

        await CollectRadiosAsync(targets, ct);
        await _insights.Roams.CollectAsync(ct);
    }

    private async Task PollAsync(ApAgentTarget target, DateTime now, CancellationToken ct)
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
            // The roam path needs the link-MAC to client-key mapping this payload carries: an MLO
            // client associates under a different MAC per link, and the events name only the link.
            _insights.Roams.NoteClients(payload);
            var accumulator = _accumulators.GetOrAdd(target.Mac, _ => new ApAgentWifiAccumulator());

            lock (accumulator)
            {
                foreach (var client in payload.Clients)
                {
                    // One point per client, never one per link: the agent has already folded an MLO
                    // client's links onto its MLD MAC.
                    var sample = ApAgentWifiFieldMapper.ToSample(client, target.Mac);
                    if (sample == null) continue;
                    accumulator.Add(sample, now);

                    // Every pass, not every write window: the cache is what Live View, the maps and
                    // a speed test trace read, and they should see 10 s old readings rather than 30.
                    PublishLive(sample, null, now, ResolvePassThroughput(sample, now));
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

    /// <summary>
    /// Publishes one reading into the site's live client cache. On an access point this agent
    /// covers, this is the authoritative live snapshot: the console wifi tier stands down for
    /// exactly the same set of access points, so a client has one source at a time, not two racing.
    /// </summary>
    /// <param name="folded">The closed window when there is one. Without it the sample carries no
    /// throughput, and claiming zero would read as an idle client rather than an unmeasured one, so
    /// whatever the last closed window established is carried forward instead.</param>
    /// <summary>
    /// Throughput since the previous pass, from the counter delta. The agent reads the counters on
    /// their own short tier, so a pass carries numbers the last one did not; before that tier the
    /// counters only moved when the identity poll ran and this yields nothing between windows.
    /// </summary>
    private (double? Tx, double? Rx) ResolvePassThroughput(ApAgentWifiSample s, DateTime now)
    {
        if (s.TxBytes is not { } tx || s.RxBytes is not { } rx) return (null, null);

        var at = s.BytesAt ?? now;
        var key = $"{s.ApMac}|{s.ClientMac}";
        double? txBps = null, rxBps = null;

        if (_passBytes.TryGetValue(key, out var prev))
        {
            var elapsed = (at - prev.At).TotalSeconds;
            var deltaTx = tx - prev.TxBytes;
            var deltaRx = rx - prev.RxBytes;

            // A counter that went backwards is an association reset, not negative traffic.
            if (elapsed > 0.5 && deltaTx >= 0 && deltaRx >= 0)
            {
                txBps = deltaTx * 8.0 / elapsed;
                rxBps = deltaRx * 8.0 / elapsed;
            }
        }

        _passBytes[key] = new PassBytes(at, tx, rx);
        return (txBps, rxBps);
    }

    /// <summary>
    /// Drops counter baselines for clients that stopped reporting. Without this a site's worth of
    /// visiting clients accumulates for the lifetime of the process.
    /// </summary>
    private void PrunePassBytes(DateTime now)
    {
        foreach (var (key, prev) in _passBytes)
        {
            if (now - prev.At > PassBytesRetention) _passBytes.TryRemove(key, out _);
        }
    }

    /// <summary>How long a counter baseline is kept for a client that has stopped reporting. Long
    /// enough that a client dropping one poll still measures against its real previous reading.</summary>
    private static readonly TimeSpan PassBytesRetention = TimeSpan.FromMinutes(5);

    private readonly record struct PassBytes(DateTime At, long TxBytes, long RxBytes);

    private void PublishLive(ApAgentWifiSample s, ApAgentWifiFolded? folded, DateTime now, (double? Tx, double? Rx) pass)
    {
        try
        {
            var live = _liveStats;
            var prior = folded == null && pass.Tx == null ? live.GetWifiClient(s.ClientMac) : null;

            live.RecordWifiClient(new WifiClientLiveSnapshot
            {
                ClientMac = s.ClientMac,
                ApMac = s.ApMac,
                Band = s.Band,
                Channel = s.Channel,
                ChannelWidth = s.ChannelWidth,
                SignalDbm = s.SignalDbm,
                NoiseDbm = s.NoiseDbm,
                TxRateKbps = s.TxRateKbps,
                RxRateKbps = s.RxRateKbps,
                TxThroughputBps = folded?.TxThroughputBps ?? pass.Tx ?? prior?.TxThroughputBps,
                RxThroughputBps = folded?.RxThroughputBps ?? pass.Rx ?? prior?.RxThroughputBps,
                Satisfaction = s.Satisfaction,
                Rssi = s.Rssi,
                IsMlo = s.IsMlo,
                Source = WifiClientSource.ApAgent,
                LastUpdate = now,
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not publish AP Agent reading to the live cache (site {Site})", _siteSlug);
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
            var s = entry.Sample;

            // The window's own throughput, measured across the whole window rather than one pass.
            PublishLive(s, entry, now, (null, null));

            // Same gate as the console path: a client that moved no traffic writes no point, so
            // swapping the source does not change how many points a site produces.
            if ((entry.TxThroughputBps ?? 0) <= 0 && (entry.RxThroughputBps ?? 0) <= 0) continue;

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

    private async Task CollectRadiosAsync(IReadOnlyList<ApAgentTarget> targets, CancellationToken ct)
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
                    r.Bandwidth,
                    r.NoiseFloor,
                    Retain(r.Counters),
                    Retain(r.Deltas),
                    r.DeltaSeconds,
                    at))
                .ToList();

            _radios[target.Mac] = radios;
            await _insights.RadioHealth.RecordAsync(target.Mac, target.Name, radios, ct);

            foreach (var r in payload.Radios)
            {
                // Serving radios only: the scan radio hops channels, and a counter-only entry has
                // no radio state - either would charge a channel with airtime it never carried.
                if (r.ScanRadio || r.CounterOnly) continue;
                if (r.Counters == null || !r.Counters.TryGetValue("cu_total", out var cuTotal)) continue;
                var cuInterf = r.Counters.TryGetValue("cu_interf", out var i) ? i : 0;
                _airtime.Record(target.Mac, r.Band ?? r.Radio, r.Channel, r.Bandwidth, cuTotal, cuInterf, at);
            }
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
}
