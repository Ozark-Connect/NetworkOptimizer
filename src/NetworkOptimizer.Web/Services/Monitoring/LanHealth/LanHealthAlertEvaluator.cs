using System.Globalization;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Web.Services.Monitoring.LanHealth;

/// <summary>
/// The LAN Health alerts: a few, nothing redundant. One alert per link, never per end: the
/// correlation key is the link id, so Incidents group by link. Muted and excluded links never
/// alert, nor do links a running Firmware Rollout wave touches (Rollout's own alerts cover them).
/// Each opening type has a recovery that closes it.
/// </summary>
public sealed class LanHealthAlertEvaluator
{
    public const string Flapping = "monitoring.link_flapping";
    public const string Stable = "monitoring.link_stable";
    public const string SpeedDegraded = "monitoring.link_speed_degraded";
    public const string SpeedRecovered = "monitoring.link_speed_recovered";
    public const string Errors = "monitoring.link_errors";
    public const string ErrorsCleared = "monitoring.link_errors_cleared";
    public const string Storm = "monitoring.broadcast_storm";
    public const string StormCleared = "monitoring.broadcast_storm_cleared";
    public const string Utilization = "monitoring.link_utilization_unusual";
    public const string UtilizationNormal = "monitoring.link_utilization_normal";

    /// <summary>Every opening type and its recovery.</summary>
    public static readonly IReadOnlyDictionary<string, string> Pairs = new Dictionary<string, string>
    {
        [Flapping] = Stable,
        [SpeedDegraded] = SpeedRecovered,
        [Errors] = ErrorsCleared,
        [Storm] = StormCleared,
        [Utilization] = UtilizationNormal,
    };

    /// <summary>Finding classes that open each alert type.</summary>
    private static readonly Dictionary<string, string[]> Classes = new()
    {
        [Flapping] = ["Link Flapping"],
        [SpeedDegraded] = ["Link Speed Degraded"],
        [Errors] = ["Link Errors"],
        [Storm] = ["Broadcast Storm", "Network Loop"],
        [Utilization] = ["Unusual Link Utilization"],
    };

    /// <summary>How long a condition must hold, both ways, before an alert opens or closes (as wan.link_speed_degraded).</summary>
    internal static readonly TimeSpan Sustain = TimeSpan.FromMinutes(5);

    private readonly IAlertEventBus? _bus;
    private readonly ILogger _logger;
    private readonly string _slug;
    private readonly string _siteSuffix;
    private readonly Dictionary<(string Type, string Link), SustainGate> _gates = new();
    private readonly OpenAlertSeed _seed;
    private bool _seeded;

    private readonly LanLinkFaults? _faults;

    public LanHealthAlertEvaluator(IAlertEventBus? bus, ILogger logger, string slug, IServiceScopeFactory? scopes, LanLinkFaults? faults = null)
    {
        _faults = faults;
        _bus = bus;
        _logger = logger;
        _slug = slug;
        _siteSuffix = slug == SiteManagementService.DefaultSiteSlug ? "" : $" (site {slug})";
        _seed = new OpenAlertSeed(scopes, slug, Pairs.Keys.ToList(), a => OpenAlertSeed.Context(a, "link_id"), logger);
    }

    /// <summary>Feeds one report. <paramref name="suppressed"/> holds link ids a rollout wave touches now.</summary>
    public async Task EvaluateAsync(LanHealthReport report, IReadOnlySet<string> suppressed, DateTime now, CancellationToken ct = default)
    {
        if (_bus == null) return;
        if (!_seeded)
        {
            var open = await _seed.OpenAsync();
            foreach (var a in open.All)
                if (Pairs.ContainsKey(a.EventType) && OpenAlertSeed.Context(a, "link_id") is { } link)
                    Gate(a.EventType, link).MarkOpen();
            _seeded = true;
        }

        var loop = report.Findings.FirstOrDefault(f => f.Class == "Network Loop");
        foreach (var link in report.Links)
        {
            var quiet = link.Exclusion != null || suppressed.Contains(link.Link.Id);
            foreach (var (type, classes) in Classes)
            {
                // Link Speed Degraded alerts on a fabric speed or duplex fault only, not an access client's slower link.
                var finding = link.Findings.FirstOrDefault(f => classes.Contains(f.Class)
                    && (type != SpeedDegraded || f.Detector is Detectors.SpeedDownshift or Detectors.DuplexMismatch));
                // One alert for a loop, not one per port: the per-port storms fold into it, and the loop's own
                // port raises the loop, not its storm.
                if (type == Storm && loop != null)
                    finding = loop.LinkId == link.Link.Id ? loop : finding?.Class == "Broadcast Storm" ? null : finding;
                var raise = finding != null && !quiet;
                var clear = finding == null;
                var gate = Gate(type, link.Link.Id);
                var t = gate.Observe(raise, clear, now, Sustain);
                if (t == GateTransition.Opened && finding != null) await PublishAsync(type, link, finding, ct);
                else if (t == GateTransition.Closed) await RecoverAsync(type, link, ct);
                // A flapping or erroring link explains loss to the device behind it: its target's loss alert stays quiet.
                if (type is Flapping or Errors && t is GateTransition.Opened or GateTransition.Closed)
                    _faults?.Set(_slug, link.Link.Downstream.DeviceMac, t == GateTransition.Opened);
                // The fault opened after a device on the link was upgraded: tie it to the rollout as a possible regression.
                if (type is Flapping or Errors && t == GateTransition.Opened && finding != null && link.UpgradeSuspect is { } u
                    && _regressionsRaised.Add((link.Link.Id, u.StepId)))
                    await PublishRegressionAsync(link, finding, u, ct);
            }
        }
    }

    private readonly HashSet<(string Link, int Step)> _regressionsRaised = [];

    private async Task PublishRegressionAsync(LinkHealth link, LinkFinding f, UpgradeSuspect u, CancellationToken ct)
    {
        var from = NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.ShortOrNull(u.FromVersion) ?? "its previous firmware";
        var to = NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.ShortOrNull(u.ToVersion) ?? "its new firmware";
        var what = f.Class == "Link Flapping" ? "started dropping" : "started showing receive errors";
        await _bus!.PublishAsync(new AlertEvent
        {
            EventType = Firmware.RolloutAlerts.LinkRegression,
            Source = "rollout",
            Severity = AlertSeverity.Warning,
            Title = $"Link Problems After Upgrade: {link.Link.Name}{_siteSuffix}",
            Message = $"{link.Link.Name} {what} after {u.DeviceName} went from {from} to {to}, and it was clean before. Power cycle {u.DeviceName} first; if that doesn't help, roll back.",
            DeviceId = link.Link.Upstream.DeviceMac,
            DeviceName = link.Link.Name,
            CorrelationKey = link.Link.Id,
            SourceUrl = LanHealthLinks.Link(link.Link.Id),
            Context = new Dictionary<string, string> { ["link_id"] = link.Link.Id, ["step_id"] = u.StepId.ToString(CultureInfo.InvariantCulture) },
        }, ct);
        _logger.LogInformation("Published possible firmware regression for {Link} after {Device} (site {Site})", link.Link.Name, u.DeviceName, _slug);
    }

    private SustainGate Gate(string type, string link)
    {
        if (!_gates.TryGetValue((type, link), out var g)) _gates[(type, link)] = g = new SustainGate();
        return g;
    }

    private async Task PublishAsync(string type, LinkHealth link, LinkFinding f, CancellationToken ct)
    {
        var severity = type switch
        {
            Utilization => AlertSeverity.Info,
            Storm when f.Class == "Network Loop" => AlertSeverity.Critical,
            Storm => AlertSeverity.Warning,
            _ => link.Link.IsFabric ? AlertSeverity.Warning : AlertSeverity.Info,
        };
        await _bus!.PublishAsync(new AlertEvent
        {
            EventType = type,
            Source = "monitoring",
            Severity = severity,
            Title = $"{f.Title}{_siteSuffix}",
            Message = f.Body,
            DeviceId = link.Link.Upstream.DeviceMac,
            DeviceName = link.Link.Name,
            CorrelationKey = link.Link.Id,
            MetricValue = link.Score,
            SourceUrl = LanHealthLinks.Link(link.Link.Id),
            Context = new Dictionary<string, string>
            {
                ["link_id"] = link.Link.Id,
                ["kind"] = link.Link.Kind.ToString(),
                ["score"] = link.Score.ToString("0.#", CultureInfo.InvariantCulture),
                ["detector"] = f.Detector.ToString(CultureInfo.InvariantCulture),
            },
        }, ct);
        _logger.LogInformation("Published {Type} for {Link} (site {Site})", type, link.Link.Name, _slug);
    }

    private async Task RecoverAsync(string type, LinkHealth link, CancellationToken ct)
    {
        var recovery = Pairs[type];
        var title = recovery switch
        {
            Stable => "Link Stable",
            SpeedRecovered => "Link Speed Recovered",
            ErrorsCleared => "Link Errors Cleared",
            StormCleared => "Broadcast Storm Cleared",
            _ => "Link Utilization Normal",
        };
        await _bus!.PublishAsync(new AlertEvent
        {
            EventType = recovery,
            Source = "monitoring",
            Severity = AlertSeverity.Info,
            Title = $"{title}: {link.Link.Name}{_siteSuffix}",
            Message = $"{link.Link.Name} is back to normal.",
            DeviceId = link.Link.Upstream.DeviceMac,
            DeviceName = link.Link.Name,
            CorrelationKey = link.Link.Id,
            MetricValue = link.Score,
            SourceUrl = LanHealthLinks.Link(link.Link.Id),
            Context = new Dictionary<string, string> { ["link_id"] = link.Link.Id },
        }, ct);
    }
}

/// <summary>Links into Monitoring - LAN Health.</summary>
public static class LanHealthLinks
{
    public static string Tab() => "/monitoring?tab=lan-health";

    public static string Link(string linkId) => $"/monitoring?tab=lan-health&link={Uri.EscapeDataString(linkId)}";
}
