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
    /// <summary>A device is "in use" when its boundary rate exceeds this (sustained 15-min mean).</summary>
    public const double ActiveThresholdBps = 200_000;

    /// <summary>Minimum history span before the fingerprint is trusted over the heuristic.</summary>
    public const int MinHistoryHours = 24;

    public const int LookbackDays = 7;

    private static readonly TimeSpan SampleWindow = TimeSpan.FromMinutes(15);

    private readonly string _siteSlug;
    private readonly MonitoringInfluxClient _influx;
    private readonly UniFiConnectionService _connection;
    private readonly ILogger<QuietWindowService> _logger;
    private readonly TimeZoneInfo _timeZone;

    public QuietWindowService(
        MonitoringInfluxRegistry influxRegistry,
        SiteConnectionRegistry siteConnections,
        ILogger<QuietWindowService> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug,
        TimeZoneInfo? timeZone = null)
    {
        var slug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
        _siteSlug = slug;
        _influx = influxRegistry.GetFor(slug);
        _connection = siteConnections.GetFor(slug);
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

        // Parent-side rows are shared by all children of one switch; query each parent once.
        var parentCache = new Dictionary<string, IReadOnlyList<MonitoringInfluxClient.InterfaceRatePoint>>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in devices)
        {
            ct.ThrowIfCancellationRequested();
            List<(DateTime Time, double Bps)> samples;
            try
            {
                samples = device.Type == DeviceType.Gateway
                    ? await GatewaySamplesAsync(device, from, to, ct)
                    : await BoundarySamplesAsync(device, from, to, parentCache, ct);
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
            var totals = new int[QuietWindowCalculator.BucketsPerWeek];
            var active = new int[QuietWindowCalculator.BucketsPerWeek];
            foreach (var (time, bps) in samples)
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(time, DateTimeKind.Utc), _timeZone);
                var bucket = (int)local.DayOfWeek * 24 + local.Hour;
                totals[bucket]++;
                if (bps > ActiveThresholdBps) active[bucket]++;
                if (earliest == null || time < earliest) earliest = time;
                if (latest == null || time > latest) latest = time;
            }
            for (var b = 0; b < busy.Length; b++)
            {
                if (totals[b] > 0) busy[b] = Math.Max(busy[b], (double)active[b] / totals[b]);
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
                "Quiet window {Site}: {Contributing}/{Devices} devices reported {Samples} samples over {Span:0.0}h but none exceeded {Threshold:N0} bps, so the window comes from the site profile",
                _siteSlug, contributing, devices.Count, totalSamples, spanHours, ActiveThresholdBps);
            return null;
        }

        _logger.LogInformation(
            "Quiet window {Site}: built from {Contributing}/{Devices} devices, {Samples} samples over {Span:0.0}h; busiest hour {Peak:P0}, {QuietHours} hours idle",
            _siteSlug, contributing, devices.Count, totalSamples, spanHours, peak, busy.Count(b => b <= 0));
        return busy;
    }

    private async Task<List<(DateTime, double)>> GatewaySamplesAsync(PlannerDevice gateway, DateTime from, DateTime to, CancellationToken ct)
    {
        var wans = await _connection.GetAllWanInterfacesAsync(ct);
        var ifNames = wans.Select(w => w.CounterIfName).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
        if (ifNames.Count == 0) return [];

        var rates = await _influx.QueryGatewayWanRatesAsync(gateway.Mac, ifNames!, from, to, SampleWindow, sumAcrossInterfaces: true, ct: ct);
        return rates
            .Where(r => r.DownloadBps.HasValue || r.UploadBps.HasValue)
            .Select(r => (r.Time, (r.DownloadBps ?? 0) + (r.UploadBps ?? 0)))
            .ToList();
    }

    private async Task<List<(DateTime, double)>> BoundarySamplesAsync(
        PlannerDevice device,
        DateTime from,
        DateTime to,
        Dictionary<string, IReadOnlyList<MonitoringInfluxClient.InterfaceRatePoint>> parentCache,
        CancellationToken ct)
    {
        var own = await _influx.QueryInterfaceRatesAsync(device.Mac, from, to, SampleWindow, ct);
        var rows = own.Where(r => IsOwnUplinkRow(device, r)).ToList();

        if (rows.Count == 0 && !string.IsNullOrEmpty(device.UplinkMac) && device.UplinkRemotePort.HasValue)
        {
            if (!parentCache.TryGetValue(device.UplinkMac, out var parentRows))
            {
                parentRows = await _influx.QueryInterfaceRatesAsync(device.UplinkMac, from, to, SampleWindow, ct);
                parentCache[device.UplinkMac] = parentRows;
            }
            rows = parentRows.Where(r => MatchesPortIndex(r, device.UplinkRemotePort.Value)).ToList();
        }

        return rows
            .Where(r => r.RateInBps.HasValue || r.RateOutBps.HasValue)
            .Select(r => (r.Time, (r.RateInBps ?? 0) + (r.RateOutBps ?? 0)))
            .ToList();
    }

    private static bool IsOwnUplinkRow(PlannerDevice device, MonitoringInfluxClient.InterfaceRatePoint row)
    {
        // APs: copper/SFP uplink interfaces only - Wi-Fi rows never count as activity.
        if (device.Type == DeviceType.AccessPoint) return IsWiredInterface(row);
        if (device.UplinkLocalPort.HasValue) return MatchesPortIndex(row, device.UplinkLocalPort.Value);
        return false;
    }

    private static bool IsWiredInterface(MonitoringInfluxClient.InterfaceRatePoint row)
    {
        var key = string.IsNullOrEmpty(row.PortId) ? row.IfName : row.PortId;
        return key.StartsWith("eth", StringComparison.OrdinalIgnoreCase) ||
               key.StartsWith("sfp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesPortIndex(MonitoringInfluxClient.InterfaceRatePoint row, int portIndex)
    {
        // Switch SNMP ifNames are "0/{port}"; gateways and some models use "eth{port}";
        // UniFi-fed rows may carry the "Port {n}" alias instead.
        var candidates = new[] { $"0/{portIndex}", $"eth{portIndex}", $"Port {portIndex}" };
        foreach (var c in candidates)
        {
            if (string.Equals(row.PortId, c, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(row.IfName, c, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
