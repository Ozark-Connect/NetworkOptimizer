using System.Collections.Concurrent;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;

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
    int? CenterMhz,
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

    /// <summary>
    /// How often membership is read, separately from the sampling pass above. Presence truth must
    /// move at the fastest path we have, and /clients serves the agent's in-memory table - the
    /// event stream and its 1 Hz sweep maintain it regardless of who asks, so a faster read costs
    /// the access point one JSON serialization and no radio or mcad work. Three seconds keeps the
    /// server's share of departure latency small against the AP's own 6 s absent grace.
    /// </summary>
    public static readonly TimeSpan MembershipInterval = TimeSpan.FromSeconds(3);

    /// <summary>Ceiling on one membership pass, so unresponsive access points cannot stack it up.</summary>
    private static readonly TimeSpan MembershipBudget = TimeSpan.FromSeconds(12);

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

    private readonly ApAgentNoiseFloorHistory _noiseFloors = new();

    /// <summary>The median noise floor over the last hour for one radio, or null until an hour's worth exists.</summary>
    public int? NoiseFloorHourMedian(string apMac, string radio) => _noiseFloors.HourMedian(apMac, radio, DateTime.UtcNow);

    private readonly ApAgentTargetDirectory _directory;
    private readonly ApAgentTelemetryClient _telemetry;
    private readonly ApAgentInsightsRegistry.SiteApAgentInsights _insights;
    private readonly MonitoringInfluxClient _influx;
    private readonly MonitoringLiveStats _liveStats;
    private readonly ILogger<ApAgentTelemetryCollector> _logger;
    private readonly string _siteSlug;

    private readonly ApAgentCoverageLedger _coverage = new();
    private readonly ApAgentMembershipLedger _membership = new();

    /// <summary>Online access points at the site, agent-covered or not, from the last pass.</summary>
    private volatile int _siteApCount;
    private readonly ApAgentAirtimeAggregator _airtime = new();
    private readonly ConcurrentDictionary<string, ApAgentWifiAccumulator> _accumulators = new(StringComparer.OrdinalIgnoreCase);
    private readonly ApAgentPassWitness _witness = new();

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
    /// The agent verdict on whether a client is associated right now, for the Console entry
    /// points. Unknown wherever the agent path cannot vouch, which hands back to the Console rules.
    /// </summary>
    public NetworkOptimizer.Core.Helpers.AgentClientPresence PresenceFor(string? apMac, string? clientMac)
    {
        var verdict = ResolvePresence(apMac, clientMac);

        // Transitions only. A bounce is a verdict changing and changing back, which is unreadable
        // from the surfaces alone and invisible if every unchanged answer is logged too.
        if (!string.IsNullOrEmpty(clientMac))
        {
            var key = clientMac.ToLowerInvariant();
            var previous = _lastVerdict.TryGetValue(key, out var p)
                ? p
                : NetworkOptimizer.Core.Helpers.AgentClientPresence.Unknown;
            if (previous != verdict)
            {
                _lastVerdict[key] = verdict;
                _logger.LogDebug(
                    "[Presence] {Client} on {Ap} (site {Site}): {Previous} -> {Verdict} (answers={Answers}/{Targets})",
                    clientMac, string.IsNullOrEmpty(apMac) ? "-" : apMac, _siteSlug, previous, verdict,
                    _membership.FreshAnswers(DateTime.UtcNow), _siteApCount);
            }
        }

        return verdict;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, NetworkOptimizer.Core.Helpers.AgentClientPresence> _lastVerdict =
        new(StringComparer.OrdinalIgnoreCase);

    private NetworkOptimizer.Core.Helpers.AgentClientPresence ResolvePresence(string? apMac, string? clientMac)
    {
        var now = DateTime.UtcNow;

        // Only a covered access point's answer may assert the not-in-a-non-empty-answer form of
        // absence. Present and the listed-as-stale form carry their own evidence, so the ledger
        // resolves them from any fresh answer, claimed access point or none at all.
        var claimed = !string.IsNullOrEmpty(apMac) && _coverage.Covers(apMac, now) ? apMac : null;
        var verdict = _membership.PresenceFor(claimed, clientMac, now);
        if (verdict != NetworkOptimizer.Core.Helpers.AgentClientPresence.Unknown) return verdict;

        // Nothing named this client and no access point was claimed. When EVERY online access
        // point at the site answered - not merely every agent-covered one - the agents can see the
        // whole site and absence from all of them is absence. On a partly covered site this stays
        // Unknown, because the client may be on an access point no agent watches.
        var aps = _siteApCount;
        return aps > 0 && _membership.FreshAnswers(now) >= aps
            ? NetworkOptimizer.Core.Helpers.AgentClientPresence.Absent
            : NetworkOptimizer.Core.Helpers.AgentClientPresence.Unknown;
    }

    /// <summary>
    /// The covered client holding this IPv4 address right now, or null. What lets Client
    /// Performance identify a client the agent has seen associate before the Console lists it.
    /// </summary>
    public ApAgentKnownClient? FindClientByIp(string ip)
    {
        var known = _membership.FindByIp(ip, DateTime.UtcNow);
        return known != null && _coverage.Covers(known.ApMac, DateTime.UtcNow) ? known : null;
    }

    /// <summary>Roster-refresh hints for consumers caching the Console's client list.</summary>
    public ConsoleRosterNudge RosterNudge { get; } = new();

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

    /// <summary>
    /// One membership pass, on its own short cadence. Reads /clients per access point and updates
    /// only the in-memory state a viewer sees change: the membership ledger (the presence verdict),
    /// the roster nudge, and the live cache. Deliberately no coverage claim, no accumulator, no
    /// witness, no throughput baseline, and no writes - the Influx cadence belongs to the sampling
    /// pass, and a faster read must not become a faster write.
    /// </summary>
    public async Task SampleMembershipAsync(CancellationToken ct = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(MembershipBudget);

        try
        {
            await MembershipCoreAsync(budget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("AP Agent membership pass ran out of budget (site {Site})", _siteSlug);
        }
    }

    private async Task MembershipCoreAsync(CancellationToken ct)
    {
        // Read-only against the ledgers on the failure side: releases and retention stay with the
        // sampling pass, so the two loops cannot duel over coverage state.
        if (!await _directory.IsSiteEnabledAsync(_siteSlug, ct)) return;

        var targets = await _directory.GetTargetsAsync(_siteSlug, ct);
        _siteApCount = _directory.CachedApCount(_siteSlug);
        if (targets.Count == 0) return;

        var now = DateTime.UtcNow;
        await Task.WhenAll(targets.Select(t => PollMembershipAsync(t, now, ct)));
    }

    private async Task PollMembershipAsync(ApAgentTarget target, DateTime now, CancellationToken ct)
    {
        await _pollGate.WaitAsync(ct);
        try
        {
            var payload = await _telemetry.GetClientsAsync(_siteSlug, target.Host, target.Token, ClientsTimeout, ct);
            if (payload == null || IsStale(payload, now)) return;

            RecordMembership(target.Mac, payload, now);
            PublishMembershipLive(target.Mac, payload, now);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AP Agent membership poll failed for {Host} (site {Site})", target.Host, _siteSlug);
        }
        finally
        {
            _pollGate.Release();
        }
    }

    /// <summary>
    /// Records one answer into the ledger and arms the roster nudge on a change. A departure is
    /// immediate: the Console still lists the client and the gate excludes it regardless, so there
    /// is nothing to wait for. An arrival keeps the settle delay - re-reading before the Console
    /// has digested the association returns the roster we already have.
    /// </summary>
    private void RecordMembership(string apMac, ApAgentClientsPayload payload, DateTime now)
    {
        if (!_membership.Record(apMac, payload.Clients, now, out var delta)) return;

        RosterNudge.NoteMembershipChange(now, immediate: delta.Left.Count > 0);
        _logger.LogDebug(
            "[Presence] agent {Ap} membership changed (site {Site}): joined [{Joined}], left [{Left}]",
            apMac, _siteSlug, string.Join(", ", delta.Joined), string.Join(", ", delta.Left));

        // The change IS the re-evaluation trigger: resolve the affected clients now, so the
        // verdict and its transition log move with the answer instead of waiting to be asked.
        foreach (var mac in delta.Joined) PresenceFor(apMac, mac);
        foreach (var mac in delta.Left) PresenceFor(apMac, mac);
    }

    /// <summary>
    /// Publishes the answer's clients into the live cache, so the maps track arrivals and RF at
    /// the membership cadence. Same gates as the sampling pass, minus everything that measures:
    /// throughput baselines stay with the pass, or its deltas would shrink to this cadence.
    /// </summary>
    private void PublishMembershipLive(string apMac, ApAgentClientsPayload payload, DateTime now)
    {
        var identityAt = payload.Sources?.Bytes?.LastCollectedAt
            ?? payload.Sources?.Slow?.LastCollectedAt
            ?? payload.CollectedAt;
        var authorizedIsReported = payload.Clients.Any(c => c.Authorized);

        foreach (var client in payload.Clients)
        {
            if (authorizedIsReported && !client.Authorized) continue;

            var sample = ApAgentWifiFieldMapper.ToSample(client, apMac, identityAt);
            if (sample == null) continue;
            if (sample.IdleSeconds is { } stale && stale > NetworkOptimizer.Core.Helpers.ClientPresence.MaxIdleSeconds) continue;
            if (_membership.IsClaimSuperseded(apMac, sample.ClientMac)) continue;

            PublishLive(sample, null, now, (null, null));
        }
    }

    private async Task SampleCoreAsync(CancellationToken ct)
    {
        if (!await _directory.IsSiteEnabledAsync(_siteSlug, ct))
        {
            _coverage.ReleaseAll();
            _membership.ReleaseAll();
            _accumulators.Clear();
            return;
        }

        var targets = await _directory.GetTargetsAsync(_siteSlug, ct);
        if (targets.Count == 0)
        {
            _coverage.ReleaseAll();
            _membership.ReleaseAll();
            return;
        }

        var targetMacs = targets.Select(t => t.Mac).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _coverage.RetainOnly(targetMacs);
        _membership.RetainOnly(targetMacs);

        var now = DateTime.UtcNow;
        var writing = now - _lastWriteAt >= WriteWindow;

        // A pass that is about to write the full point does not also write a thin one: the fold
        // covers that instant already, and two points for it would only disagree.
        _witness.Reset();
        await Task.WhenAll(targets.Select(t => PollAsync(t, now, writing, ct)));
        ReportContestedClients();

        if (!writing) return;
        _lastWriteAt = now;

        PrunePassBytes(now);

        if (!_influx.IsConfigured) await _influx.ReconfigureAsync(ct);
        foreach (var target in targets)
            WriteFolded(target.Mac, now);

        await CollectRadiosAsync(targets, ct);
        await _insights.Roams.CollectAsync(ct);
    }

    private async Task PollAsync(ApAgentTarget target, DateTime now, bool writingFullPoint, CancellationToken ct)
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
                _membership.Release(target.Mac);
                _accumulators.TryRemove(target.Mac, out _);
                return;
            }

            // Dated by the pass that read the fields, not by when the reply was built, or a rate
            // the access point read an interval ago is recorded as current. Bytes owns the only
            // mca-dump from binary version 10; slow is the same read on older agents.
            var identityAt = payload.Sources?.Bytes?.LastCollectedAt
                ?? payload.Sources?.Slow?.LastCollectedAt
                ?? payload.CollectedAt;

            // The envelope stays fresh while a tier underneath it fails - mca-dump returns nothing
            // usable while an access point reprovisions - so the station read's own age decides
            // whether this payload can vouch for the clients. Claiming on the envelope alone held
            // the console out of an access point the agent had stopped describing.
            if (now - identityAt > ApAgentCoverageLedger.ClaimTtl)
            {
                _coverage.Release(target.Mac);
                _membership.Release(target.Mac);
                _accumulators.TryRemove(target.Mac, out _);
                return;
            }

            _coverage.Claim(target.Mac, now);
            RecordMembership(target.Mac, payload, now);

            // The roam path needs the link-MAC to client-key mapping this payload carries: an MLO
            // client associates under a different MAC per link, and the events name only the link.
            _insights.Roams.NoteClients(payload);
            var accumulator = _accumulators.GetOrAdd(target.Mac, _ => new ApAgentWifiAccumulator());

            // Firmware that does not report the flag leaves every client false, so requiring it
            // would drop the whole site. Only trust it where something in this payload is true.
            var authorizedIsReported = payload.Clients.Any(c => c.Authorized);

            lock (accumulator)
            {
                foreach (var client in payload.Clients)
                {
                    // One point per client, never one per link: the agent has already folded an MLO
                    // client's links onto its MLD MAC.
                    var sample = ApAgentWifiFieldMapper.ToSample(client, target.Mac, identityAt);
                    if (sample == null) continue;

                    // Before the gates on purpose: a claim they are about to drop is exactly the
                    // kind we want to see when two access points disagree.
                    _witness.Claimed(sample.ClientMac, target.Mac, sample.IdleSeconds,
                        sample.SignalDbm, client.Authorized, sample.Band);

                    // An association that never completed is not a client. The access point lists it
                    // with a real signal reading, which is how one device ends up drawn on several.
                    if (authorizedIsReported && !client.Authorized) continue;

                    // BEFORE the accumulator, never after. WriteFolded publishes every folded entry
                    // into the live cache, so a client the access point has not heard from went back
                    // onto the map once per write window and survived there until the add path found
                    // its entry too stale - in and out on a thirty second beat.
                    if (sample.IdleSeconds is { } stale && stale > NetworkOptimizer.Core.Helpers.ClientPresence.MaxIdleSeconds) continue;

                    // A discarded claim is a dead entry for a client that associated elsewhere;
                    // writing or publishing it repaints the client onto the wrong access point.
                    if (_membership.IsClaimSuperseded(target.Mac, sample.ClientMac)) continue;

                    accumulator.Add(sample, now);

                    // Every pass, not every write window: the cache is what Live View, the maps and
                    // a speed test trace read, and they should see 10 s old readings rather than 30.
                    var pass = ResolvePassThroughput(sample, now);
                    PublishLive(sample, null, now, pass);

                    // Throughput is stored as often as it is measured; everything else keeps the
                    // write window, because it describes how a client is connected rather than
                    // what it is doing. Same gate as the full point: no traffic, no point.
                    if (!writingFullPoint && ((pass.Tx ?? 0) > 0 || (pass.Rx ?? 0) > 0))
                    {
                        _ = _influx.WriteWifiClientThroughputAsync(
                            apMac: sample.ApMac,
                            band: sample.Band,
                            clientMac: sample.ClientMac,
                            txThroughputBps: pass.Tx ?? 0,
                            rxThroughputBps: pass.Rx ?? 0,
                            signalDbm: sample.SignalDbm,
                            timestamp: StampFor(sample.BytesAt ?? sample.CollectedAt, now),
                            txRateKbps: sample.TxRateKbps,
                            rxRateKbps: sample.RxRateKbps);
                    }
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
            _membership.Release(target.Mac);
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

        var resolved = _passBytes.TryGetValue(key, out var prev)
            ? ApAgentThroughput.FromCounters(tx, rx, at, prev.TxBytes, prev.RxBytes, prev.At)
            : (null, null);

        _passBytes[key] = new PassBytes(at, tx, rx);
        return resolved;
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
            // Always consulted. A null rate means the counters could not support one - too little
            // time between readings, or a reset - and the cache coerces a null throughput to 0, so
            // publishing one turns "not measured" into "idle". That is the flat 0/0 between bursts:
            // a real rate, then a window that could not be measured overwriting it with zero.
            var prior = live.GetWifiClient(s.ClientMac);

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
                IdleSeconds = s.IdleSeconds,
                Source = WifiClientSource.ApAgent,
                LastUpdate = now,
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not publish AP Agent reading to the live cache (site {Site})", _siteSlug);
        }
    }

    /// <summary>
    /// Clock difference past which an access point's own timestamps are refused. A wedged clock
    /// would scatter its points across the series rather than fail visibly.
    /// </summary>
    private static readonly TimeSpan MaxAgentClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>The reading's own time where it is plausible, the server's where it is not.</summary>
    private static DateTime StampFor(DateTime? collectedAt, DateTime now)
    {
        if (collectedAt is not { } at) return now;

        // AsUtc, never ToUniversalTime: the agent sends UTC, but a value that arrives Unspecified
        // would be read as local and shifted by the container's offset.
        var utc = NetworkOptimizer.Core.Helpers.DateTimeUtilities.AsUtc(at);
        return (utc - now).Duration() > MaxAgentClockSkew ? now : utc;
    }

    /// <summary>
    /// Reports any client two access points both claimed this pass. Warning rather than debug: it
    /// means we wrote a point per access point for one association, and the map redraws the client
    /// onto whichever answered last.
    /// </summary>
    private void ReportContestedClients()
    {
        var contested = _witness.Contested();
        if (contested.Count == 0) return;

        foreach (var line in contested)
            _logger.LogWarning("Client claimed by several access points on site {Site} - {Claims}", _siteSlug, line);
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

            // A client that moved nothing still writes, carrying presence and signal but no rate.
            // Skipping it entirely is why an idle client read back as departed: playback cannot
            // tell "connected and quiet" from "gone" when neither writes anything.
            //
            // Except a client the access point has not actually heard from. It holds one
            // associated long after it physically left - measured at 50 minutes idle, signal at the
            // noise floor, still authorised, with the console agreeing - and presence for that
            // draws a device a town away on the map forever.
            //
            // Idle time, NOT "never carried traffic": that test fails on multi-link. An MLO client
            // associates once per band under its own randomised MAC, and one link carrying a few
            // bytes at association makes the whole client look alive indefinitely, because the
            // active-link pick deliberately prefers the link that did carry something.
            if ((entry.TxThroughputBps ?? 0) <= 0 && (entry.RxThroughputBps ?? 0) <= 0)
            {
                if (s.IdleSeconds is { } idle && idle > NetworkOptimizer.Core.Helpers.ClientPresence.MaxIdleSeconds) continue;

                _ = _influx.WriteWifiClientThroughputAsync(
                    apMac: s.ApMac,
                    band: s.Band,
                    clientMac: s.ClientMac,
                    txThroughputBps: null,
                    rxThroughputBps: null,
                    signalDbm: s.SignalDbm,
                    timestamp: StampFor(s.CollectedAt, now).AddTicks(tickOffset++),
                    txRateKbps: s.TxRateKbps,
                    rxRateKbps: s.RxRateKbps);
                continue;
            }

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
                timestamp: StampFor(s.CollectedAt, now).AddTicks(tickOffset++),
                txRetries: s.TxRetries,
                txAttempts: s.TxAttempts,
                txDropped: s.TxDropped,
                latencyAvgMs: s.LatencyAvgMs,
                latencyMaxMs: s.LatencyMaxMs,
                tcpStalls: s.TcpStalls,
                tcpLatAvgMs: s.TcpLatAvgMs,
                idleSeconds: s.IdleSeconds,
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
                    r.CenterMhz,
                    r.NoiseFloor,
                    Retain(r.Counters),
                    Retain(r.Deltas),
                    r.DeltaSeconds,
                    at))
                .ToList();

            _radios[target.Mac] = radios;
            foreach (var r in payload.Radios.Where(r => !r.ScanRadio && !r.CounterOnly))
                _noiseFloors.Record(target.Mac, r.Name, r.NoiseFloor, at);
            await _insights.RadioHealth.RecordAsync(target.Mac, target.Name, radios, ct);
            _insights.ChannelMoves.NoteRadios(target.Mac, target.Name, radios);

            foreach (var r in payload.Radios)
            {
                // Serving radios only: the scan radio hops channels, and a counter-only entry has
                // no radio state - either would charge a channel with airtime it never carried.
                if (r.ScanRadio || r.CounterOnly) continue;
                if (r.Counters == null || !r.Counters.TryGetValue("cu_total", out var cuTotal)) continue;
                var cuInterf = r.Counters.TryGetValue("cu_interf", out var i) ? i : 0;
                var band = RadioBandExtensions.FromUniFiCode(ApAgentAirtimeAggregator.MapBandCode(r.Band ?? r.Radio));
                int? center = r.CenterMhz is { } mhz ? ChannelSpanHelper.CenterChannelFromMhz(band, mhz) : null;
                _airtime.Record(target.Mac, r.Band ?? r.Radio, r.Channel, r.Bandwidth, cuTotal, cuInterf, at, center, r.NoiseFloor);
            }
        }

        // Any move whose hour is up gets its verdict from the hours folded above.
        _insights.ChannelMoves.EvaluateOutcomes(_airtime, DateTime.UtcNow);
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
