using System.Globalization;
using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.ChangeControl.SiteHealth;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>Whether a WAN is quiet enough for upstream discovery to judge reachability.</summary>
/// <param name="Ok">True when nothing objects.</param>
/// <param name="Reason">What objects, phrased to follow "The WAN is busy right now (...)"; null when Ok.</param>
public sealed record UpstreamDiscoveryGateVerdict(bool Ok, string? Reason = null)
{
    /// <summary>Nothing objects.</summary>
    public static readonly UpstreamDiscoveryGateVerdict Clear = new(true);
}

/// <summary>
/// The health check before an upstream discovery run. Discovery's reachability gate pings every
/// candidate, so on a WAN that is down, saturated, or losing packets, hops that are fine fail it and
/// read as unreachable. Automatic runs wait for a quiet WAN; a manual run warns and lets the operator go on.
/// </summary>
public class UpstreamDiscoveryGate
{
    /// <summary>How far back load and loss are read.</summary>
    internal static readonly TimeSpan Lookback = TimeSpan.FromMinutes(10);

    private const string OutageEventType = "monitoring.wan_outage";
    private const string PartialEventType = "monitoring.wan_outage_partial";
    private const string AllWansDeviceId = "all-wans";
    private const string SystemActor = "upstream-discovery";

    private readonly IspHealthRegistry _ispHealth;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UpstreamDiscoveryGate> _logger;

    public UpstreamDiscoveryGate(IspHealthRegistry ispHealth, IServiceScopeFactory scopeFactory, ILogger<UpstreamDiscoveryGate> logger)
    {
        _ispHealth = ispHealth;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Evaluates one WAN of a site: an open outage alert for it, its load over the last
    /// <see cref="Lookback"/>, and its loss pool's loss over the same span. Any input that cannot be
    /// read counts as clear.
    /// </summary>
    /// <param name="slug">Site.</param>
    /// <param name="wanInterface">The WAN key ("wan", "wan2"), or null for the site's primary.</param>
    /// <param name="ct">Cancellation.</param>
    public virtual async Task<UpstreamDiscoveryGateVerdict> EvaluateAsync(string slug, string? wanInterface, CancellationToken ct = default)
    {
        var isp = _ispHealth.GetFor(slug, wanInterface);
        var to = DateTime.UtcNow;
        var from = to - Lookback;

        string? wanKey = null;
        try { wanKey = GatewayWanHelper.WanInterfaceKeyFromKey(await isp.GetScoredWanKeyAsync(ct)); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Upstream discovery gate: could not resolve the WAN for site {Slug}", slug);
        }

        var outage = await IsOutageOpenAsync(slug, wanKey, ct);

        double? utilization = null;
        try { utilization = await isp.GetMedianUtilizationAsync(from, to, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Upstream discovery gate: could not read WAN load for site {Slug}", slug);
        }

        double? loss = null;
        try { loss = await isp.GetPoolLossPercentAsync(from, to, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Upstream discovery gate: could not read loss for site {Slug}", slug);
        }

        return Judge(outage, utilization, loss, isp.Options.LoadedThresholdFraction);
    }

    /// <summary>
    /// The verdict over the gate's inputs. Load blocks at ISP Health's loaded threshold, not below it:
    /// busy networks run near it all day and would never discover. Null load or loss never blocks.
    /// </summary>
    /// <param name="outageOpen">An outage or partial-outage alert is open for the WAN.</param>
    /// <param name="utilization">Median utilization, fraction of plan (worst direction), or null.</param>
    /// <param name="lossPct">Loss pool loss, percent, or null.</param>
    /// <param name="loadedFraction">ISP Health's loaded threshold.</param>
    internal static UpstreamDiscoveryGateVerdict Judge(bool outageOpen, double? utilization, double? lossPct, double loadedFraction)
    {
        if (outageOpen)
            return new(false, "an outage is in progress");
        if (utilization is double u && u >= loadedFraction)
            return new(false, $"running at {(u * 100).ToString("0", CultureInfo.InvariantCulture)}% of plan");
        if (lossPct is double l && l > SiteHealthScorer.NoticeableLossPct)
            return new(false, $"{l.ToString("0.#", CultureInfo.InvariantCulture)}% packet loss in the last 10 minutes");
        return UpstreamDiscoveryGateVerdict.Clear;
    }

    /// <summary>Whether an active alert says this WAN, or every WAN of the site, is out.</summary>
    internal static bool HasOpenOutage(IEnumerable<AlertHistoryEntry> alerts, string? wanKey) =>
        alerts.Any(a => a.Status == AlertStatus.Active
            && a.EventType is OutageEventType or PartialEventType
            && (string.Equals(a.DeviceId, AllWansDeviceId, StringComparison.OrdinalIgnoreCase)
                || (wanKey != null && !string.IsNullOrEmpty(a.DeviceId)
                    && string.Equals(GatewayWanHelper.WanInterfaceKeyFromKey(a.DeviceId.Trim()), wanKey, StringComparison.OrdinalIgnoreCase))));

    private async Task<bool> IsOutageOpenAsync(string slug, string? wanKey, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(slug);
            using var system = Identity.SystemScope.Enter(scope.ServiceProvider, SystemActor);
            var alerts = scope.ServiceProvider.GetRequiredService<IAlertRepository>();
            return HasOpenOutage(await alerts.GetActiveAlertsAsync(ct), wanKey);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Upstream discovery gate: could not read active alerts for site {Slug}", slug);
            return false;
        }
    }
}
