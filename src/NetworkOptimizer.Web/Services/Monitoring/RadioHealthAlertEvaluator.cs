using System.Collections.Concurrent;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Web.Services.ApAgent;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Raises alerts for the AP Agent radio counters: the CCA wedge itself, and the elevated 6 GHz
/// TX PDEV reset-counter rate observed before one measured wedge.
///
/// This is the most defensible "you could not have known" case we have, because the UniFi Console
/// shows nothing at all while a radio is wedged and nothing reaches dmesg or syslog either.
/// </summary>
public class RadioHealthAlertEvaluator
{
    /// <summary>The radio is transmitting nothing while reporting the medium permanently busy.</summary>
    public const string WedgeEventType = "monitoring.radio_wedged";

    /// <summary>The 6 GHz TX PDEV reset-counter rate is elevated above its own baseline.</summary>
    public const string ResetEventType = "monitoring.radio_resets";

    private readonly IAlertEventBus _eventBus;
    private readonly ILogger<RadioHealthAlertEvaluator> _logger;
    private readonly string _siteSuffix;
    private readonly ConcurrentDictionary<string, ApAgentRadioWedgeDetector> _detectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _resetAlertedAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Each radio's established TX PDEV counter increments per minute, keyed "apMac:radio".
    /// Held in memory: losing it on a restart costs a few passes of quiet, whereas persisting a
    /// stale baseline could make a changed radio alert immediately on arrival.
    /// </summary>
    private readonly ConcurrentDictionary<string, double> _resetBaseline = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A counter-rate warning repeats no more often than this while the pattern holds.</summary>
    private static readonly TimeSpan ResetRepeat = TimeSpan.FromHours(6);

    /// <param name="eventBus">The site's alert bus.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <param name="siteSlug">
    /// Site this instance evaluates for (one instance per site, owned by
    /// <see cref="MonitoringAlertRegistry"/>). Non-default sites get their slug appended to titles.
    /// </param>
    public RadioHealthAlertEvaluator(
        IAlertEventBus eventBus,
        ILogger<RadioHealthAlertEvaluator> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _eventBus = eventBus;
        _logger = logger;
        _siteSuffix = string.IsNullOrEmpty(siteSlug) || siteSlug == SiteManagementService.DefaultSiteSlug
            ? "" : $" (site {siteSlug})";
    }

    /// <summary>Evaluates one access point's radios for a pass.</summary>
    public async ValueTask EvaluateAsync(
        string apMac, string? apName, IReadOnlyList<ApRadioWindow> windows, CancellationToken ct = default)
    {
        var detector = _detectors.GetOrAdd(apMac, _ => new ApAgentRadioWedgeDetector());

        foreach (var window in windows)
        {
            if (!detector.Observe(window.Radio, window.Wedged)) continue;
            await PublishWedgeAsync(apMac, apName, window, ct);
        }

        var baselines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in windows)
        {
            if (w.PdevResetDelta is null || w.WindowSeconds <= 0) continue;
            var bkey = $"{apMac}:{w.Radio}";
            if (_resetBaseline.TryGetValue(bkey, out var known)) baselines[w.Radio] = known;

            // Fold this pass in afterwards, so a window is judged against the baseline that existed
            // before it and cannot raise the bar it is being measured against.
            var rate = w.PdevResetDelta.Value / (w.WindowSeconds / 60.0);
            _resetBaseline[bkey] = _resetBaseline.TryGetValue(bkey, out var prev)
                ? (prev * 0.9) + (rate * 0.1)
                : rate;
        }

        foreach (var window in ApAgentRadioWedgeDetector.ElevatedResets(windows, baselines))
        {
            var key = $"{apMac}:{window.Radio}";
            if (_resetAlertedAt.TryGetValue(key, out var last) && DateTime.UtcNow - last < ResetRepeat) continue;
            _resetAlertedAt[key] = DateTime.UtcNow;
            await PublishResetsAsync(apMac, apName, window, ct);
        }
    }

    private async ValueTask PublishWedgeAsync(
        string apMac, string? apName, ApRadioWindow window, CancellationToken ct)
    {
        var label = Describe(apName, apMac, window);
        _logger.LogWarning("Radio wedge on {Ap} {Radio}: rx_clear {RxClear} of cycle {Cycle}, tx_frame 0",
            apMac, window.Radio, window.RxClearDelta, window.CycleDelta);

        await _eventBus.PublishAsync(new AlertEvent
        {
            EventType = WedgeEventType,
            Source = "monitoring",
            Severity = AlertSeverity.Error,
            Title = $"Radio stopped transmitting on {label}{_siteSuffix}",
            Message =
                $"The {label} radio reports the channel busy for {window.BusyRatio:P1} of cycles while transmitting "
                + $"nothing at all over the last {window.WindowSeconds:0} seconds. Clients abandon a band in this "
                + "state and neither the UniFi Console nor the access point's own logs show anything. Restarting "
                + "the radio, or the access point, clears it.",
            DeviceId = apMac,
            DeviceName = apName,
            MetricValue = window.BusyRatio,
            ThresholdValue = ApAgentRadioWedgeDetector.BusyRatioThreshold,
            SourceUrl = MonitoringLinks.DeviceStats(apMac, MonitoringLinks.NowMs()),
            Tags = ["monitoring", "wifi", "radio"],
            Context = new Dictionary<string, string>
            {
                ["device_mac"] = apMac,
                ["radio"] = window.Radio,
                ["band"] = window.Band ?? "",
                ["channel"] = window.Channel.ToString(),
                ["cycle_delta"] = window.CycleDelta?.ToString() ?? "",
                ["rx_clear_delta"] = window.RxClearDelta?.ToString() ?? "",
            }
        }, ct);
    }

    private async ValueTask PublishResetsAsync(
        string apMac, string? apName, ApRadioWindow window, CancellationToken ct)
    {
        var label = Describe(apName, apMac, window);

        await _eventBus.PublishAsync(new AlertEvent
        {
            EventType = ResetEventType,
            Source = "monitoring",
            Severity = AlertSeverity.Warning,
            Title = $"TX reset counter elevated on {label}{_siteSuffix}",
            Message =
                $"The {label} firmware TX PDEV reset counter rose by {window.PdevResetDelta} over "
                + $"{window.WindowSeconds:0} seconds, above this radio's recent baseline. This does not by "
                + "itself mean the radio restarted or clients lost service. A sustained increase preceded "
                + "one measured 6 GHz radio wedge; check client and transmit history before taking action.",
            DeviceId = apMac,
            DeviceName = apName,
            MetricValue = window.PdevResetDelta,
            SourceUrl = MonitoringLinks.DeviceStats(apMac, MonitoringLinks.NowMs()),
            Tags = ["monitoring", "wifi", "radio"],
            Context = new Dictionary<string, string>
            {
                ["device_mac"] = apMac,
                ["radio"] = window.Radio,
                ["band"] = window.Band ?? "",
                ["pdev_resets"] = window.PdevResets?.ToString() ?? "",
            }
        }, ct);
    }

    private static string Describe(string? apName, string apMac, ApRadioWindow window)
    {
        var device = string.IsNullOrEmpty(apName) ? apMac : apName;
        return string.IsNullOrEmpty(window.Band) ? $"{device} {window.Radio}" : $"{device} {window.Band} GHz";
    }
}
