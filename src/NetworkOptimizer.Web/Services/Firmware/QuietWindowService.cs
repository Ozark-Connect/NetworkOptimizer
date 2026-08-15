using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// Builds the 168-bucket hour-of-week busy fingerprint for the devices in a rollout and
/// proposes the quietest start window. Activity is measured exactly like the 2D map's
/// boundary aggregate: the rate on each device's bounding uplink port, never a sum over
/// all its interfaces; AP Wi-Fi interfaces are ignored entirely (wired uplink only).
/// </summary>
public class QuietWindowService
{
    /// <summary>Minimum history span before the fingerprint is trusted over the heuristic.</summary>
    public const int MinHistoryHours = 24;

    /// <summary>
    /// Four weeks folded into one hour-of-week picture, so each hour is judged on about four
    /// samples of that same weekday and hour rather than one.
    /// </summary>
    public const int LookbackDays = 28;

    private static readonly TimeSpan SampleWindow = TimeSpan.FromMinutes(15);

    /// <summary>How long history reading gets before the site profile answers instead.</summary>
    private static readonly TimeSpan HistoryBudget = TimeSpan.FromSeconds(15);

    private readonly string _siteSlug;
    private readonly MonitoringInfluxClient _influx;
    private readonly ILogger<QuietWindowService> _logger;
    private readonly TimeZoneInfo _timeZone;

    public QuietWindowService(
        MonitoringInfluxRegistry influxRegistry,
        ILogger<QuietWindowService> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug,
        TimeZoneInfo? timeZone = null,
        string? consoleTimeZoneId = null)
    {
        var slug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
        _siteSlug = slug;
        _influx = influxRegistry.GetFor(slug);
        _logger = logger;
        // Hours of the week only mean anything in the site's own timezone; the server may be
        // nowhere near it.
        _timeZone = timeZone ?? ResolveTimeZone(consoleTimeZoneId, logger, slug);
    }

    /// <summary>Carries the site's zone out with the proposal, and the instant it names in UTC.</summary>
    private QuietWindowProposal Stamp(QuietWindowProposal proposal) => new()
    {
        Day = proposal.Day,
        Hour = proposal.Hour,
        StartLocal = proposal.StartLocal,
        StartUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(proposal.StartLocal, DateTimeKind.Unspecified), _timeZone),
        TimeZoneId = _timeZone.Id,
        BusyScore = proposal.BusyScore,
        UsedFallback = proposal.UsedFallback,
        Basis = proposal.Basis,
    };

    private static TimeZoneInfo ResolveTimeZone(string? id, ILogger logger, string slug)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Local;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            logger.LogWarning("Quiet window {Site}: unknown console timezone {Zone}, using this server's", slug, id);
            return TimeZoneInfo.Local;
        }
    }

    /// <summary>
    /// Proposes a start window for a rollout of the given estimated duration. Pinned
    /// (Fixed) mode bypasses history; otherwise history is used when it spans at least
    /// <see cref="MinHistoryHours"/>, and the home/business heuristic fills in when not.
    /// </summary>
    public async Task<QuietWindowProposal> ProposeAsync(
        IReadOnlyList<PlannerDevice> devices,
        int rolloutDurationSeconds,
        FirmwareRolloutSettings settings,
        int clientCount,
        TimeSpan minLead,
        CancellationToken ct = default)
    {
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _timeZone);

        if (settings.AutopilotWindowMode == FirmwareAutopilotWindowMode.Fixed &&
            settings.FixedDayOfWeek is >= 0 and <= 6 && settings.FixedHour is >= 0 and <= 23)
        {
            return Stamp(QuietWindowCalculator.Fixed((DayOfWeek)settings.FixedDayOfWeek.Value, settings.FixedHour.Value, nowLocal, minLead));
        }

        double[]? fingerprint = null;
        try
        {
            fingerprint = await BuildBusyFingerprintAsync(devices, ct);
        }
        // Only the caller giving up propagates. Anything else - a slow query, a dead bucket - is a
        // missing suggestion, never a failure to plan: this runs on the path that opens the wizard.
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("Quiet-window fingerprint timed out; falling back to profile heuristic");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Quiet-window fingerprint failed; falling back to profile heuristic");
        }

        if (fingerprint != null)
        {
            return Stamp(QuietWindowCalculator.FindBest(fingerprint, rolloutDurationSeconds, nowLocal, minLead));
        }

        var apCount = devices.Count(d => d.Type == DeviceType.AccessPoint);
        var switchCount = devices.Count(d => d.Type == DeviceType.Switch);
        var profile = SiteProfileClassifier.Classify(devices.Count, apCount, switchCount, clientCount);
        return Stamp(QuietWindowCalculator.Fallback(profile, nowLocal, minLead));
    }

    /// <summary>
    /// 168-bucket busy fraction, or null when history spans under <see cref="MinHistoryHours"/>.
    /// A bucket's value is the busiest single device's active fraction there - the window
    /// must be quiet for every device it will take down.
    /// </summary>
    public async Task<double[]?> BuildBusyFingerprintAsync(IReadOnlyList<PlannerDevice> devices, CancellationToken ct = default)
    {
        // Aligned to the sample window: unaligned edges put the two aggregation passes on
        // different boundaries, and the same query minutes apart returns different buckets.
        var to = Floor(DateTime.UtcNow, SampleWindow);
        var from = to.AddDays(-LookbackDays);
        var busy = new double[QuietWindowCalculator.BucketsPerWeek];
        DateTime? earliest = null, latest = null;
        var anyData = false;
        var contributing = 0;
        var totalSamples = 0;

        var samplesByMac = await AllDeviceSamplesAsync(devices, from, to, ct);

        foreach (var device in devices)
        {
            ct.ThrowIfCancellationRequested();
            if (!samplesByMac.TryGetValue(MacNormalizer.Normalize(device.Mac), out var samples))
            {
                _logger.LogDebug("Quiet window {Site}: {Name} ({Mac}) contributed no samples", _siteSlug, device.Name, device.Mac);
                continue;
            }
            if (samples.Count == 0)
            {
                _logger.LogDebug("Quiet window {Site}: {Name} ({Mac}) contributed no samples", _siteSlug, device.Name, device.Mac);
                continue;
            }

            anyData = true;
            contributing++;
            totalSamples += samples.Count;

            // Mean throughput per bucket, then scaled against this device's own busiest hour. A
            // busy switch and a quiet AP then contribute on the same 0..1 scale, and the score is
            // how loaded an hour is rather than whether anything moved at all.
            var totals = new int[QuietWindowCalculator.BucketsPerWeek];
            var sums = new double[QuietWindowCalculator.BucketsPerWeek];
            foreach (var (time, bps) in samples)
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(time, DateTimeKind.Utc), _timeZone);
                var bucket = (int)local.DayOfWeek * 24 + local.Hour;
                totals[bucket]++;
                sums[bucket] += bps;
                if (earliest == null || time < earliest) earliest = time;
                if (latest == null || time > latest) latest = time;
            }

            var means = new double[QuietWindowCalculator.BucketsPerWeek];
            for (var b = 0; b < means.Length; b++)
            {
                if (totals[b] > 0) means[b] = sums[b] / totals[b];
            }

            var devicePeak = means.Max();
            if (devicePeak <= 0)
            {
                _logger.LogDebug("Quiet window {Site}: {Name} moved no traffic all week", _siteSlug, device.Name);
                continue;
            }

            for (var b = 0; b < busy.Length; b++)
            {
                // The window has to be quiet for every device it takes down, so the busiest
                // device in an hour sets that hour's score.
                busy[b] = Math.Max(busy[b], means[b] / devicePeak);
            }
        }

        if (!anyData || earliest == null || latest == null)
        {
            _logger.LogInformation(
                "Quiet window {Site}: no usage history for any of {Devices} devices, so the window comes from the site profile",
                _siteSlug, devices.Count);
            return null;
        }

        var spanHours = (latest.Value - earliest.Value).TotalHours;
        if (spanHours < MinHistoryHours)
        {
            _logger.LogInformation(
                "Quiet window {Site}: history spans {Span:0.0}h, under the {Min}h minimum, so the window comes from the site profile",
                _siteSlug, spanHours, MinHistoryHours);
            return null;
        }

        // Every bucket flat at zero is not a quiet week, it is a week nothing crossed the activity
        // threshold. Scoring it picks whichever hour comes first and calls it evidence.
        var peak = busy.Max();
        if (peak <= 0)
        {
            _logger.LogInformation(
                "Quiet window {Site}: {Contributing}/{Devices} devices reported {Samples} samples over {Span:0.0}h but none moved any traffic, so the window comes from the site profile",
                _siteSlug, contributing, devices.Count, totalSamples, spanHours);
            return null;
        }

        _logger.LogInformation(
            "Quiet window {Site}: built from {Contributing}/{Devices} devices, {Samples} samples over {Span:0.0}h; quietest hour {Min:P0} of peak load, median {Median:P0}",
            _siteSlug, contributing, devices.Count, totalSamples, spanHours, busy.Min(),
            busy.OrderBy(b => b).ElementAt(busy.Length / 2));
        return busy;
    }

    /// <summary>
    /// A device's activity, computed the way the 2D map computes Ingress/Egress: the sum of
    /// rate_in and rate_out across its monitored interfaces at each sample instant, mirroring
    /// MonitoringCollectionAgent's fabric seed. For a gateway that counts LAN and WAN together.
    /// </summary>
    /// <remarks>
    /// APs are the one exception, exactly as the live path treats them: their radio interfaces
    /// over-count beacons, retries and MIMO duplicates, so only the copper and SFP uplinks count.
    /// </remarks>
    /// <summary>
    /// Every device's per-window throughput, in two queries rather than one per device: access
    /// points count their wired side only, everything else counts every interface. The cost is
    /// then flat in the size of the site, which is what a large site cannot afford to have scale.
    /// </summary>
    private async Task<Dictionary<string, List<(DateTime Time, double Bps)>>> AllDeviceSamplesAsync(
        IReadOnlyList<PlannerDevice> devices, DateTime from, DateTime to, CancellationToken ct)
    {
        var byMac = new Dictionary<string, List<(DateTime Time, double Bps)>>(StringComparer.OrdinalIgnoreCase);
        var apMacs = devices.Where(d => d.Type == DeviceType.AccessPoint).Select(d => d.Mac).ToList();
        var otherMacs = devices.Where(d => d.Type != DeviceType.AccessPoint).Select(d => d.Mac).ToList();

        // The proposal is advisory and the wizard cannot open without it, so history is bounded
        // rather than waited on: past the budget the site profile picks the window instead. The
        // client's own timeout is a minute, which is far longer than a user will sit on a spinner.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(HistoryBudget);

        foreach (var (macs, wiredOnly) in new[] { (otherMacs, false), (apMacs, true) })
        {
            if (macs.Count == 0) continue;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var rows = await _influx.QueryDeviceRateTotalsAsync(macs, from, to, SampleWindow, wiredOnly, budget.Token);
                foreach (var row in rows)
                {
                    var key = MacNormalizer.Normalize(row.DeviceMac);
                    if (!byMac.TryGetValue(key, out var list))
                        byMac[key] = list = [];
                    list.Add((row.Time, row.Bps));
                }
                _logger.LogDebug(
                    "Quiet window {Site}: read {Rows} totals for {Count} devices (wiredOnly={Wired}) in {Ms} ms",
                    _siteSlug, rows.Count, macs.Count, wiredOnly, timer.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Quiet window {Site}: history for {Count} devices did not answer within {Budget}s, "
                    + "so the window comes from the site profile",
                    _siteSlug, macs.Count, HistoryBudget.TotalSeconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Quiet-window: reading history for {Count} devices failed", macs.Count);
            }
        }

        return byMac;
    }

    /// <summary>Truncates an instant down to the previous boundary of the given size.</summary>
    private static DateTime Floor(DateTime value, TimeSpan interval) =>
        new(value.Ticks - value.Ticks % interval.Ticks, value.Kind);
}
