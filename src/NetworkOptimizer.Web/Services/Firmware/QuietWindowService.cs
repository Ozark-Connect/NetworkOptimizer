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

    public const int LookbackDays = 7;

    private static readonly TimeSpan SampleWindow = TimeSpan.FromMinutes(15);

    private readonly string _siteSlug;
    private readonly MonitoringInfluxClient _influx;
    private readonly ILogger<QuietWindowService> _logger;
    private readonly TimeZoneInfo _timeZone;

    public QuietWindowService(
        MonitoringInfluxRegistry influxRegistry,
        ILogger<QuietWindowService> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug,
        TimeZoneInfo? timeZone = null)
    {
        var slug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
        _siteSlug = slug;
        _influx = influxRegistry.GetFor(slug);
        _logger = logger;
        _timeZone = timeZone ?? TimeZoneInfo.Local;
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
            return QuietWindowCalculator.Fixed((DayOfWeek)settings.FixedDayOfWeek.Value, settings.FixedHour.Value, nowLocal, minLead);
        }

        double[]? fingerprint = null;
        try
        {
            fingerprint = await BuildBusyFingerprintAsync(devices, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Quiet-window fingerprint failed; falling back to profile heuristic");
        }

        if (fingerprint != null)
        {
            return QuietWindowCalculator.FindBest(fingerprint, rolloutDurationSeconds, nowLocal, minLead);
        }

        var apCount = devices.Count(d => d.Type == DeviceType.AccessPoint);
        var switchCount = devices.Count(d => d.Type == DeviceType.Switch);
        var profile = SiteProfileClassifier.Classify(devices.Count, apCount, switchCount, clientCount);
        return QuietWindowCalculator.Fallback(profile, nowLocal, minLead);
    }

    /// <summary>
    /// 168-bucket busy fraction, or null when history spans under <see cref="MinHistoryHours"/>.
    /// A bucket's value is the busiest single device's active fraction there - the window
    /// must be quiet for every device it will take down.
    /// </summary>
    public async Task<double[]?> BuildBusyFingerprintAsync(IReadOnlyList<PlannerDevice> devices, CancellationToken ct = default)
    {
        var to = DateTime.UtcNow;
        var from = to.AddDays(-LookbackDays);
        var busy = new double[QuietWindowCalculator.BucketsPerWeek];
        DateTime? earliest = null, latest = null;
        var anyData = false;
        var contributing = 0;
        var totalSamples = 0;

        foreach (var device in devices)
        {
            ct.ThrowIfCancellationRequested();
            List<(DateTime Time, double Bps)> samples;
            try
            {
                samples = await DeviceSamplesAsync(device, from, to, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Quiet-window: no usable history for {Mac}", device.Mac);
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
    private async Task<List<(DateTime Time, double Bps)>> DeviceSamplesAsync(
        PlannerDevice device, DateTime from, DateTime to, CancellationToken ct)
    {
        var rows = await _influx.QueryInterfaceRatesAsync(device.Mac, from, to, SampleWindow, ct);
        var usable = device.Type == DeviceType.AccessPoint
            ? rows.Where(IsWiredInterface)
            : rows;

        return usable
            .GroupBy(r => r.Time)
            .Select(g => (Time: g.Key, Bps: g.Sum(r => (r.RateInBps ?? 0) + (r.RateOutBps ?? 0))))
            .OrderBy(p => p.Time)
            .ToList();
    }

    /// <summary>Copper and SFP uplinks; a radio interface is never a wired uplink.</summary>
    private static bool IsWiredInterface(MonitoringInfluxClient.InterfaceRatePoint row)
    {
        var key = string.IsNullOrEmpty(row.PortId) ? row.IfName : row.PortId;
        return key.StartsWith("eth", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("sfp", StringComparison.OrdinalIgnoreCase);
    }
}
