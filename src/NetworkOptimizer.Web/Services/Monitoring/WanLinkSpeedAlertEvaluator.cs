using System.Globalization;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// One gateway WAN port as of one fresh console snapshot, with the speed it is expected to reach.
/// </summary>
/// <param name="DeviceMac">Gateway MAC, normalized lower-case.</param>
/// <param name="DeviceName">Gateway name.</param>
/// <param name="IfName">Kernel interface name of the port.</param>
/// <param name="PortName">The port's name in UniFi.</param>
/// <param name="WanLabel">The WAN's display name, when known.</param>
/// <param name="SpeedMbps">Negotiated speed; null when the port is down.</param>
/// <param name="FullDuplex">Negotiated duplex, when reported.</param>
/// <param name="Autoneg">Whether the port auto-negotiates (false = speed forced in UniFi).</param>
/// <param name="ExpectedMbps">The speed this port normally negotiates; null when not yet known.</param>
/// <param name="ExpectedFromPlan">
/// Whether <paramref name="ExpectedMbps"/> comes from the WAN's plan speed rather than the port's
/// own history, so the alert does not claim the port has reached a speed it was never seen at.
/// </param>
public sealed record WanLinkSpeedObservation(
    string DeviceMac,
    string DeviceName,
    string IfName,
    string PortName,
    string? WanLabel,
    int? SpeedMbps,
    bool? FullDuplex,
    bool Autoneg,
    int? ExpectedMbps,
    bool ExpectedFromPlan = false)
{
    /// <summary>The name the alert uses for this WAN: its label, else the gateway port.</summary>
    public string Label => string.IsNullOrWhiteSpace(WanLabel) ? $"{DeviceName} {PortName}" : WanLabel!;
}

/// <summary>
/// Publishes <c>wan.link_speed_degraded</c> when a gateway WAN port negotiates below the speed it
/// normally reaches, or at half duplex, and <c>wan.link_speed_recovered</c> when it is back. A bad
/// cable, a failing connector, or an auto-negotiation fault with the ISP's equipment silently caps
/// the connection this way, and nothing else in UniFi raises it.
/// </summary>
public class WanLinkSpeedAlertEvaluator
{
    /// <summary>Event type published when a WAN port drops below its usual speed.</summary>
    public const string DegradedEventType = "wan.link_speed_degraded";

    /// <summary>Event type published when it is back. Closes the degraded alert on the same port.</summary>
    public const string RecoveredEventType = "wan.link_speed_recovered";

    /// <summary>
    /// How long a downshift (or its recovery) must hold. A renegotiation during a reboot or a cable
    /// reseat settles well inside this, and the rule is about a link that stays capped.
    /// </summary>
    internal static readonly TimeSpan Sustain = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Longest gap between two snapshots of a port that still continues a sustain run. Snapshots
    /// arrive every console poll; a longer gap (console unreachable) means the run was not watched,
    /// so it restarts rather than counting the gap as time held.
    /// </summary>
    internal static readonly TimeSpan MaxSnapshotGap = TimeSpan.FromMinutes(3);

    private readonly IAlertEventBus _eventBus;
    private readonly ILogger<WanLinkSpeedAlertEvaluator> _logger;
    private readonly string _siteSuffix;
    private readonly Dictionary<string, PortState> _states = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="eventBus">Site-stamped alert bus.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="siteSlug">Site this instance evaluates for (one per site, owned by the registry).</param>
    public WanLinkSpeedAlertEvaluator(
        IAlertEventBus eventBus,
        ILogger<WanLinkSpeedAlertEvaluator> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _eventBus = eventBus;
        _logger = logger;
        _siteSuffix = string.IsNullOrEmpty(siteSlug) || siteSlug == SiteManagementService.DefaultSiteSlug
            ? "" : $" (site {siteSlug})";
    }

    private sealed class PortState
    {
        public readonly SustainGate Gate = new();
        public DateTime? LastSnapshotAt;
        public int? PublishedSpeedMbps;
        public bool? PublishedFullDuplex;
    }

    /// <summary>The alert key for one gateway port: carried as the alert's DeviceId.</summary>
    public static string PortKey(string deviceMac, string ifName) => $"{deviceMac}/{ifName}";

    /// <summary>
    /// Feed one fresh snapshot of a WAN port. Called from the site's single medium-tier loop, so
    /// the per-port state needs no locking.
    /// </summary>
    /// <param name="observation">The port as of the snapshot.</param>
    /// <param name="now">Current time, UTC.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask EvaluateAsync(WanLinkSpeedObservation observation, DateTime now, CancellationToken ct = default)
    {
        var key = PortKey(observation.DeviceMac, observation.IfName);
        if (!_states.TryGetValue(key, out var state))
            _states[key] = state = new PortState();

        if (state.LastSnapshotAt is { } last && now - last > MaxSnapshotGap)
            state.Gate.Stall();
        state.LastSnapshotAt = now;

        // A down port, or one with no known usual speed yet, says nothing about negotiation.
        // Link loss is the WAN outage alerts' job.
        if (observation.SpeedMbps is not int speed || observation.ExpectedMbps is not int expected || expected <= 0)
        {
            state.Gate.Stall();
            return;
        }

        var halfDuplex = observation.FullDuplex == false;
        var degraded = speed < expected || halfDuplex;
        var transition = state.Gate.Observe(degraded, !degraded, now, Sustain);

        if (transition == GateTransition.Closed)
        {
            state.PublishedSpeedMbps = null;
            state.PublishedFullDuplex = null;
            await PublishRecoveredAsync(observation, speed, ct);
            return;
        }

        // Re-publish an open alert only on a further drop, which supersedes the one already open.
        var furtherDrop = state.Gate.Open && transition == GateTransition.None
            && (speed < state.PublishedSpeedMbps || (halfDuplex && state.PublishedFullDuplex != false));
        if (transition != GateTransition.Opened && !furtherDrop) return;

        state.PublishedSpeedMbps = speed;
        state.PublishedFullDuplex = observation.FullDuplex;
        await PublishDegradedAsync(observation, speed, expected, halfDuplex, ct);
    }

    private async ValueTask PublishDegradedAsync(
        WanLinkSpeedObservation o, int speed, int expected, bool halfDuplex, CancellationToken ct)
    {
        var negotiated = $"{speed.ToString(CultureInfo.InvariantCulture)} Mbps{(halfDuplex ? " half duplex" : "")}";
        // A plan-derived expectation was never seen on this port, so the ISP device may simply not
        // support it; say so rather than blaming the cable outright.
        var advice = o.ExpectedFromPlan
            ? $"Check that the ISP's equipment and the cable support {expected.ToString(CultureInfo.InvariantCulture)} Mbps. " +
              (o.Autoneg
                  ? "If they do, try setting the port's link speed manually in UniFi Network."
                  : "The port's speed is already set manually in UniFi Network.")
            : o.Autoneg
                ? "This is usually a cable, a connector, or auto-negotiation with the ISP's equipment. If it keeps happening, try setting the port's link speed manually in UniFi Network."
                : "The port's speed is already set manually in UniFi Network, so check the cable and connectors.";

        await _eventBus.PublishAsync(new AlertEvent
        {
            EventType = DegradedEventType,
            Source = "wan",
            Severity = AlertSeverity.Warning,
            Title = $"WAN Link Speed Degraded: {o.Label}{_siteSuffix}",
            Message = $"{o.DeviceName} port {o.PortName} ({o.IfName}) has negotiated {negotiated}, " +
                (o.ExpectedFromPlan
                    ? $"below the {expected.ToString(CultureInfo.InvariantCulture)} Mbps its internet plan needs. {advice}"
                    : $"where it normally reaches {expected.ToString(CultureInfo.InvariantCulture)} Mbps. {advice}"),
            DeviceId = PortKey(o.DeviceMac, o.IfName),
            DeviceName = o.Label,
            MetricValue = speed,
            ThresholdValue = expected,
            SourceUrl = MonitoringLinks.DeviceStats(o.DeviceMac, MonitoringLinks.NowMs()),
            Context = Context(o, new Dictionary<string, string>
            {
                ["link_speed_mbps"] = speed.ToString(CultureInfo.InvariantCulture),
                ["expected_mbps"] = expected.ToString(CultureInfo.InvariantCulture),
                ["full_duplex"] = halfDuplex ? "false" : "true",
            })
        }, ct);

        _logger.LogInformation(
            "Published {EventType} for {Gateway} {IfName}: {Negotiated}, expected {Expected} Mbps",
            DegradedEventType, o.DeviceName, o.IfName, negotiated, expected);
    }

    private async ValueTask PublishRecoveredAsync(WanLinkSpeedObservation o, int speed, CancellationToken ct)
    {
        await _eventBus.PublishAsync(new AlertEvent
        {
            EventType = RecoveredEventType,
            Source = "wan",
            Severity = AlertSeverity.Info,
            Title = $"WAN Link Speed Recovered: {o.Label}{_siteSuffix}",
            Message = $"{o.DeviceName} port {o.PortName} ({o.IfName}) is negotiating " +
                $"{speed.ToString(CultureInfo.InvariantCulture)} Mbps, its usual speed.",
            DeviceId = PortKey(o.DeviceMac, o.IfName),
            DeviceName = o.Label,
            MetricValue = speed,
            SourceUrl = MonitoringLinks.DeviceStats(o.DeviceMac, MonitoringLinks.NowMs()),
            Context = Context(o, new Dictionary<string, string>
            {
                ["link_speed_mbps"] = speed.ToString(CultureInfo.InvariantCulture),
            })
        }, ct);

        _logger.LogInformation("Published {EventType} for {Gateway} {IfName}: {Speed} Mbps",
            RecoveredEventType, o.DeviceName, o.IfName, speed);
    }

    private static Dictionary<string, string> Context(WanLinkSpeedObservation o, Dictionary<string, string> extra)
    {
        extra["device_mac"] = o.DeviceMac;
        extra["if_name"] = o.IfName;
        extra["port_name"] = o.PortName;
        if (!string.IsNullOrWhiteSpace(o.WanLabel)) extra["wan_label"] = o.WanLabel!;
        return extra;
    }
}
