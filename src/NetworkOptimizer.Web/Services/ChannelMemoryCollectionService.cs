using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.WiFi;
using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Background collector for the Channel Recommendation outcome memory. Periodically pulls
/// hourly per-AP radio metrics and channel-change events from the UniFi Console, attributes
/// each sample to the (channel, width) that was actually live at the time, and persists
/// daily aggregates - building a long-term measured record of how each tried config really
/// performed that outlives the console's short metrics retention. Also maintains the
/// persisted channel-change log the soak-period suppression reads.
///
/// Data-integrity rules: all fetches throw on failure (an empty result must mean "no data",
/// never "fetch failed" - a silent failure would misattribute samples or skip a window), a
/// failed cycle persists nothing and leaves the watermark untouched so the window is
/// re-collected next time, and samples + change records + watermark commit in one
/// transaction so a partial write can never double-count on retry.
/// </summary>
public class ChannelMemoryCollectionService : BackgroundService
{
    /// <summary>Delay before the first collection so startup isn't burdened.</summary>
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Collection cadence. Hourly UniFi metrics are retained for days, so an occasional
    /// sweep loses nothing; 6 hours keeps the change log fresh enough for soak tracking
    /// while staying negligible load.
    /// </summary>
    private static readonly TimeSpan CollectionInterval = TimeSpan.FromHours(6);

    /// <summary>UniFi hourly report retention we can safely reach back into.</summary>
    private static readonly TimeSpan MaxLookback = TimeSpan.FromDays(7);

    /// <summary>How long outcome buckets and superseded change records are kept.</summary>
    private const int RetentionDays = 365;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IChannelMemoryRepository _repository;
    private readonly UniFiConnectionService _connectionService;
    private readonly ILogger<ChannelMemoryCollectionService> _logger;
    private readonly string _siteSlug;

    public ChannelMemoryCollectionService(
        IServiceProvider serviceProvider,
        IServiceScopeFactory scopeFactory,
        SiteConnectionRegistry siteConnections,
        ILogger<ChannelMemoryCollectionService> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _scopeFactory = scopeFactory;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
        var isDefault = _siteSlug == SiteManagementService.DefaultSiteSlug;
        _connectionService = siteConnections.GetFor(_siteSlug);
        // Build this site's own repository (per-site DB); DI's forwarded repository would
        // resolve to the default site inside the background scope (no ambient HttpContext).
        _repository = ActivatorUtilities.CreateInstance<NetworkOptimizer.Storage.Repositories.ChannelMemoryRepository>(
            serviceProvider, _siteSlug, isDefault);
        _logger = logger;
    }

    /// <summary>No-op: ChannelMemoryRegistry owns start/stop for each site's collector.</summary>
    public override void Dispose() { }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CollectAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Nothing was persisted and the watermark did not move - the whole
                    // window is re-collected on the next cycle.
                    _logger.LogWarning(ex, "Channel outcome memory collection cycle failed; window will be retried");
                }

                await Task.Delay(CollectionInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    private async Task CollectAsync(CancellationToken cancellationToken)
    {
        if (!_connectionService.IsConnected)
        {
            _logger.LogDebug("Channel memory collection skipped - not connected to UniFi Console");
            return;
        }

        var now = DateTimeOffset.UtcNow;

        using var scope = _scopeFactory.CreateScope();
        // Pin the scope to this collector's site so WiFiOptimizerService (and everything it
        // resolves) targets this site's console/DB, not the default site's.
        scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(_siteSlug);
        var wifiService = scope.ServiceProvider.GetRequiredService<WiFiOptimizerService>();

        var aps = await wifiService.GetAccessPointsAsync();
        var onlineAps = aps.Where(ap => ap.IsOnline).ToList();
        if (onlineAps.Count == 0)
        {
            _logger.LogDebug("Channel memory collection skipped - no online APs");
            return;
        }

        // Neighbor memory first, in its own try/catch and BEFORE the watermark early-return:
        // sightings are upserts keyed by last-seen (no watermark involved), so they must run
        // every cycle even when no new whole hour of metrics exists yet, a metrics failure
        // below must not starve them, and a sighting failure must not abort the metrics cycle.
        try
        {
            await PersistNeighborSightingsAsync(wifiService, now, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Neighbor sighting persistence failed; will retry next cycle");
        }

        // UniFi stamps hourly report rows at interval START, so a row is only complete once
        // the next hour begins. Collect strictly whole hours: the window ends at the last
        // hour boundary, and the watermark records that boundary - rows in [start, collectEnd)
        // are aggregated exactly once, and the in-progress hour is picked up complete next cycle.
        var collectEnd = new DateTimeOffset(
            now.UtcDateTime.Date.AddHours(now.UtcDateTime.Hour), TimeSpan.Zero);
        var earliest = collectEnd - MaxLookback;
        var watermark = await _repository.GetCollectionWatermarkAsync(cancellationToken);
        var start = watermark.HasValue && watermark.Value > earliest.UtcDateTime
            ? new DateTimeOffset(DateTime.SpecifyKind(watermark.Value, DateTimeKind.Utc))
            : earliest;

        if (collectEnd <= start)
            return;

        // Events are fetched over the full lookback (not just the collection window) so the
        // attribution timeline knows which channel was live at the window's start. Both
        // fetches throw on failure: proceeding with silently-empty events would attribute
        // every sample to the current channel, and silently-empty metrics would advance the
        // watermark past a window UniFi still has.
        var fetchTasks = onlineAps.Select(async ap =>
        {
            var metricsTask = wifiService.GetApMetricsAsync(
                new[] { ap.Mac }, start, collectEnd, MetricGranularity.Hourly, throwOnFailure: true);
            var eventsTask = wifiService.GetChannelChangeEventsAsync(earliest, now, ap.Mac, throwOnFailure: true);
            await Task.WhenAll(metricsTask, eventsTask);
            return (Ap: ap, Metrics: metricsTask.Result, Events: eventsTask.Result);
        });
        var fetched = await Task.WhenAll(fetchTasks);

        var latestConfigs = await _repository.GetLatestConfigsAsync(cancellationToken);
        var bands = new[] { RadioBand.Band2_4GHz, RadioBand.Band5GHz, RadioBand.Band6GHz };
        var samples = new List<ChannelOutcomeSample>();
        var newChanges = new List<ApChannelChange>();

        foreach (var (ap, metrics, events) in fetched)
        {
            var macLower = ap.Mac.ToLowerInvariant();

            foreach (var band in bands)
            {
                var radio = ap.Radios.FirstOrDefault(r => r.Band == band && r.Channel.HasValue);
                if (radio == null) continue;

                var bandCode = band.ToUniFiCode();
                var currentChannel = radio.Channel!.Value;
                var currentWidth = radio.ChannelWidth ?? 0;
                var bandEvents = events
                    .Where(e => e.Band == band)
                    .OrderBy(e => e.Timestamp)
                    .ToList();

                var lastKnown = latestConfigs.FirstOrDefault(c =>
                    c.ApMac.Equals(macLower, StringComparison.OrdinalIgnoreCase) && c.Band == bandCode);

                // The radio's current width can only be claimed for samples provably inside
                // the CURRENT config period: either the period started at a change event in
                // the event window (width observed now presumed to hold since then), or the
                // last persisted config already had this channel at this width (both
                // endpoints agree, no events in between). Anything else records width 0
                // (unknown) - the merge treats unknown as compatible, so the data still
                // counts; it just doesn't assert a specific width it can't know.
                var lastEvent = bandEvents.Count > 0 ? bandEvents[^1] : null;
                DateTimeOffset? widthValidFrom = null;
                if (lastEvent != null && lastEvent.NewChannel == currentChannel)
                    widthValidFrom = lastEvent.Timestamp;
                else if (lastEvent == null && lastKnown != null &&
                         lastKnown.NewChannel == currentChannel &&
                         lastKnown.NewWidthMhz.HasValue && lastKnown.NewWidthMhz.Value == currentWidth)
                    widthValidFrom = DateTimeOffset.MinValue;

                // Attribute each hourly sample to the channel live at its timestamp.
                // [start, collectEnd) matches the watermark semantics - no loss, no double count.
                foreach (var metric in metrics)
                {
                    if (metric.Timestamp < start || metric.Timestamp >= collectEnd) continue;
                    if (!metric.ByBand.TryGetValue(band, out var bandData) ||
                        !bandData.ChannelUtilization.HasValue)
                        continue;

                    var channel = ChannelMemoryHelper.GetChannelAtTime(metric.Timestamp, bandEvents, currentChannel);
                    // An event parsed without PREVIOUS_CHANNEL yields channel 0 for samples
                    // predating it - not a real channel, don't create buckets for it.
                    if (channel <= 0) continue;

                    var width = channel == currentChannel &&
                                widthValidFrom.HasValue && metric.Timestamp >= widthValidFrom.Value
                        ? currentWidth
                        : 0;
                    samples.Add(new ChannelOutcomeSample(
                        macLower,
                        bandCode,
                        channel,
                        width,
                        metric.Timestamp.UtcDateTime,
                        bandData.ChannelUtilization ?? 0,
                        bandData.Interference ?? 0,
                        bandData.TxRetryPct ?? 0));
                }

                // Maintain the persisted change log. The last persisted record per radio is
                // the de-duplication high-water: only events newer than it are appended.
                if (lastKnown == null)
                {
                    newChanges.Add(new ApChannelChange
                    {
                        ApMac = macLower,
                        Band = bandCode,
                        NewChannel = currentChannel,
                        NewWidthMhz = currentWidth > 0 ? currentWidth : null,
                        ChangedAtUtc = now.UtcDateTime,
                        Source = ApChannelChangeSource.Initial
                    });
                    continue;
                }

                var lastRecordedChannel = lastKnown.NewChannel;
                var lastRecordedWidth = lastKnown.NewWidthMhz;
                var lastRecordedAt = DateTime.SpecifyKind(lastKnown.ChangedAtUtc, DateTimeKind.Utc);
                foreach (var evt in bandEvents.Where(e => e.Timestamp.UtcDateTime > lastRecordedAt))
                {
                    var isCurrentConfig = evt.NewChannel == currentChannel && evt == lastEvent;
                    newChanges.Add(new ApChannelChange
                    {
                        ApMac = macLower,
                        Band = bandCode,
                        PreviousChannel = evt.PreviousChannel > 0 ? evt.PreviousChannel : null,
                        NewChannel = evt.NewChannel,
                        NewWidthMhz = isCurrentConfig && currentWidth > 0 ? currentWidth : null,
                        ChangedAtUtc = evt.Timestamp.UtcDateTime,
                        Source = ApChannelChangeSource.UniFiEvent
                    });
                    lastRecordedChannel = evt.NewChannel;
                    lastRecordedWidth = isCurrentConfig && currentWidth > 0 ? currentWidth : null;
                }

                if (lastRecordedChannel != currentChannel)
                {
                    // The live config disagrees with everything recorded - the UniFi log
                    // missed the change (or it aged out). The real change time is unknown:
                    // if the gap reaches past the event-log window the change almost
                    // certainly predates it (any change inside the window would be in the
                    // events), so stamp it at the last recorded time rather than fabricate a
                    // fresh soak for a config that may be fully soaked already. Inside the
                    // window, detection latency is bounded by one collection cycle.
                    var observedAt = lastRecordedAt <= earliest.UtcDateTime
                        ? lastRecordedAt
                        : Max(lastRecordedAt, (now - CollectionInterval).UtcDateTime);
                    newChanges.Add(new ApChannelChange
                    {
                        ApMac = macLower,
                        Band = bandCode,
                        PreviousChannel = lastRecordedChannel,
                        PreviousWidthMhz = lastRecordedChannel == lastKnown.NewChannel ? lastKnown.NewWidthMhz : null,
                        NewChannel = currentChannel,
                        NewWidthMhz = currentWidth > 0 ? currentWidth : null,
                        ChangedAtUtc = observedAt,
                        Source = ApChannelChangeSource.Observed
                    });
                }
                else if (currentWidth > 0 && (lastRecordedWidth ?? 0) != currentWidth)
                {
                    // Width-only change (same channel) or a width first becoming known. UniFi
                    // emits no event for these, so record it ourselves - it resets the
                    // width-confidence boundary above so future cycles can claim the current
                    // width (and stop claiming an old one). Harmless for soak: the soak
                    // builder ignores records whose previous channel equals the current one.
                    newChanges.Add(new ApChannelChange
                    {
                        ApMac = macLower,
                        Band = bandCode,
                        PreviousChannel = currentChannel,
                        PreviousWidthMhz = lastRecordedWidth,
                        NewChannel = currentChannel,
                        NewWidthMhz = currentWidth,
                        ChangedAtUtc = Max(lastRecordedAt, (now - CollectionInterval).UtcDateTime),
                        Source = ApChannelChangeSource.Observed
                    });
                }
            }
        }

        await _repository.CommitCollectionAsync(samples, newChanges, collectEnd.UtcDateTime, cancellationToken);
        await _repository.PruneAsync(RetentionDays, ChannelMemoryHelper.NeighborRetentionDays, cancellationToken);

        _logger.LogDebug(
            "Channel memory collection: {Samples} samples across {Aps} APs, {Changes} new change records " +
            "(window {Start:MM/dd HH:mm} - {End:MM/dd HH:mm} UTC)",
            samples.Count, onlineAps.Count, newChanges.Count, start, collectEnd);
    }

    /// <summary>
    /// Persist the current neighbor picture into the long-term neighbor memory. UniFi's
    /// rogue/neighbor table only carries recently-seen networks, so each cycle upserts what
    /// the console still remembers; rows for neighbors that stay invisible stop updating and
    /// age out. Own-network BSSIDs are modeled internally by the engine and are not stored.
    /// </summary>
    private async Task PersistNeighborSightingsAsync(
        WiFiOptimizerService wifiService, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var scans = await wifiService.GetChannelScanResultsAsync(startTime: now - MaxLookback);
        if (scans.Count == 0) return;

        var samples = new List<NeighborSightingSample>();
        foreach (var scan in scans)
        {
            var bandCode = scan.Band.ToUniFiCode();
            if (string.IsNullOrEmpty(bandCode)) continue;

            foreach (var nb in scan.Neighbors)
            {
                if (string.IsNullOrEmpty(nb.Bssid) || nb.IsOwnNetwork) continue;
                if (nb.Channel <= 0 || !nb.Signal.HasValue) continue;
                if (nb.Signal.Value < ChannelMemoryHelper.MinPersistedNeighborSignalDbm) continue;

                samples.Add(new NeighborSightingSample(
                    scan.ApMac.ToLowerInvariant(),
                    bandCode,
                    nb.Bssid.ToLowerInvariant(),
                    nb.Channel,
                    nb.Width ?? 0,
                    nb.Signal.Value,
                    (nb.LastSeen ?? now).UtcDateTime,
                    nb.Ssid));
            }
        }

        if (samples.Count == 0) return;
        await _repository.UpsertNeighborSightingsAsync(samples, cancellationToken);
        _logger.LogDebug("Neighbor memory: upserted {Count} sightings", samples.Count);
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
