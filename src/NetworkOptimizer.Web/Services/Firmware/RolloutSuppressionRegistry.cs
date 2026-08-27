using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// Remembers which devices are inside their own firmware rollout window, so the standard
/// <c>device.offline</c> and <c>device.rebooted</c> alerts stay quiet for a restart the rollout
/// asked for. The rollout's own alerts always flow; this only mutes the generic ones, and only
/// while the plan has <c>SuppressStandardAlerts</c> on. It also carries the AP Agent hold
/// (<see cref="RefreshAgentHold"/>), which is independent of that flag: not redeploying a binary
/// at a flashing access point is never a preference.
///
/// Same shape and the same deliberate staleness as <c>DeviceTransitionTracker</c>: the orchestrator
/// refreshes an entry on every pass while a device's window is open, so if the orchestrator stops,
/// crashes, or loses the site, suppression LAPSES within
/// <see cref="WindowFreshness"/> and alerting resumes. Failing to alert is worse than alerting
/// during an upgrade. <see cref="Clear"/> ends a window immediately once a step settles.
///
/// MAC keys strip separators, matching <c>DeviceTransitionTracker</c> - the writer here and the
/// readers in the evaluators must agree, or suppression silently does nothing.
/// </summary>
public class RolloutSuppressionRegistry
{
    /// <summary>
    /// How long one refresh keeps suppressing. Comfortably longer than the orchestrator's poll
    /// cadence, short enough that a forgotten entry cannot mute a real outage for long.
    /// </summary>
    public static readonly TimeSpan WindowFreshness = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<(string Site, string Mac), DateTime> _windowRefreshedAt = new();
    private readonly ConcurrentDictionary<(string Site, string Mac), DateTime> _agentHoldAt = new();
    private readonly ConcurrentDictionary<string, DateTime> _consoleCyclingAt = new();
    private readonly ConcurrentDictionary<string, DateTime> _osCyclingAt = new();
    private readonly ConcurrentDictionary<string, DateTime> _siteActiveAt = new();

    /// <summary>
    /// Marks the entire site as cycling a console-level step (Network app or UniFi OS update).
    /// Every device goes dark during these, not just the ones being firmware-upgraded.
    /// </summary>
    public void RefreshConsoleCycle(string siteSlug, DateTime observedAt) =>
        _consoleCyclingAt[NormalizeSite(siteSlug)] = observedAt.ToUniversalTime();

    /// <summary>Ends the console-cycle window for a site.</summary>
    public void ClearConsoleCycle(string siteSlug) =>
        _consoleCyclingAt.TryRemove(NormalizeSite(siteSlug), out _);

    /// <summary>
    /// Marks the site as cycling a UniFi OS update. The gateway reboots during these, taking
    /// WAN connectivity with it, so WAN outage alerts are suppressed in addition to the
    /// device/target alerts that <see cref="RefreshConsoleCycle"/> already covers.
    /// </summary>
    public void RefreshOsCycle(string siteSlug, DateTime observedAt) =>
        _osCyclingAt[NormalizeSite(siteSlug)] = observedAt.ToUniversalTime();

    /// <summary>Whether the site's gateway is mid-UniFi OS update (WAN expected to be down).</summary>
    public bool IsOsCycling(string siteSlug, DateTime now)
    {
        var site = NormalizeSite(siteSlug);
        return _osCyclingAt.TryGetValue(site, out var at) && now.ToUniversalTime() - at <= WindowFreshness;
    }

    /// <summary>
    /// Marks the site as having an active rollout. Any device reboot during a rollout can
    /// take downstream devices dark, so monitoring target alerts are suppressed site-wide.
    /// </summary>
    public void RefreshSiteActive(string siteSlug, DateTime observedAt) =>
        _siteActiveAt[NormalizeSite(siteSlug)] = observedAt.ToUniversalTime();

    /// <summary>Whether any rollout activity is happening on this site.</summary>
    public bool IsSiteActiveRollout(string siteSlug, DateTime now)
    {
        var site = NormalizeSite(siteSlug);
        var utcNow = now.ToUniversalTime();
        return (_siteActiveAt.TryGetValue(site, out var at) && utcNow - at <= WindowFreshness)
            || (_consoleCyclingAt.TryGetValue(site, out var cycleAt) && utcNow - cycleAt <= WindowFreshness);
    }

    /// <summary>
    /// Marks a device as inside its rollout window as of now. Called every pass while the device's
    /// step is in flight.
    /// </summary>
    /// <param name="siteSlug">Site the device belongs to.</param>
    /// <param name="deviceMac">Device MAC in any format.</param>
    /// <param name="observedAt">When the window was last confirmed open.</param>
    public void Refresh(string siteSlug, string? deviceMac, DateTime observedAt)
    {
        if (string.IsNullOrWhiteSpace(deviceMac)) return;
        _windowRefreshedAt[(NormalizeSite(siteSlug), Normalize(deviceMac))] = observedAt.ToUniversalTime();
    }

    /// <summary>
    /// Marks the device's AP Agent as held off while its own upgrade step is in flight, so the
    /// agent's supervisor does not push the binary back at a device that is flashing or booting.
    /// Unlike the alert windows, never gated on <c>SuppressStandardAlerts</c>; same staleness rule,
    /// so a crashed rollout releases the hold within <see cref="WindowFreshness"/>.
    /// </summary>
    /// <param name="siteSlug">Site the device belongs to.</param>
    /// <param name="deviceMac">Device MAC in any format.</param>
    /// <param name="observedAt">When the hold was last confirmed open.</param>
    public void RefreshAgentHold(string siteSlug, string? deviceMac, DateTime observedAt)
    {
        if (string.IsNullOrWhiteSpace(deviceMac)) return;
        _agentHoldAt[(NormalizeSite(siteSlug), Normalize(deviceMac))] = observedAt.ToUniversalTime();
    }

    /// <summary>Whether this device's AP Agent is held off by an in-flight rollout step.</summary>
    /// <param name="siteSlug">Site the device belongs to.</param>
    /// <param name="deviceMac">Device MAC in any format.</param>
    /// <param name="now">Current time.</param>
    public bool IsAgentHeld(string siteSlug, string? deviceMac, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(deviceMac)) return false;
        return _agentHoldAt.TryGetValue((NormalizeSite(siteSlug), Normalize(deviceMac)), out var at)
            && now.ToUniversalTime() - at <= WindowFreshness;
    }

    /// <summary>
    /// Ends a device's window at once, so a settled step is alertable again without waiting out
    /// the staleness. Releases the AP Agent hold too: a settled step means the redeploy is welcome.
    /// </summary>
    /// <param name="siteSlug">Site the device belongs to.</param>
    /// <param name="deviceMac">Device MAC in any format.</param>
    public void Clear(string siteSlug, string? deviceMac)
    {
        if (string.IsNullOrWhiteSpace(deviceMac)) return;
        var key = (NormalizeSite(siteSlug), Normalize(deviceMac));
        _windowRefreshedAt.TryRemove(key, out _);
        _agentHoldAt.TryRemove(key, out _);
    }

    /// <summary>
    /// Whether this device is inside a rollout window that was confirmed recently enough to trust.
    /// </summary>
    /// <param name="siteSlug">Site the device belongs to.</param>
    /// <param name="deviceMac">Device MAC in any format.</param>
    /// <param name="now">Current time.</param>
    public bool IsInRolloutWindow(string siteSlug, string? deviceMac, DateTime now)
    {
        var site = NormalizeSite(siteSlug);
        var utcNow = now.ToUniversalTime();

        if (_consoleCyclingAt.TryGetValue(site, out var cycleAt) && utcNow - cycleAt <= WindowFreshness)
            return true;

        if (string.IsNullOrWhiteSpace(deviceMac)) return false;

        if (!_windowRefreshedAt.TryGetValue((site, Normalize(deviceMac)), out var at))
            return false;

        return utcNow - at <= WindowFreshness;
    }

    /// <summary>Drops every window a site holds (rollout finished, aborted, or the site went away).</summary>
    /// <param name="siteSlug">Site to clear.</param>
    public void ClearSite(string siteSlug)
    {
        var site = NormalizeSite(siteSlug);
        _siteActiveAt.TryRemove(site, out _);
        _consoleCyclingAt.TryRemove(site, out _);
        _osCyclingAt.TryRemove(site, out _);
        foreach (var key in _windowRefreshedAt.Keys.Where(k => k.Site == site).ToList())
            _windowRefreshedAt.TryRemove(key, out _);
        foreach (var key in _agentHoldAt.Keys.Where(k => k.Site == site).ToList())
            _agentHoldAt.TryRemove(key, out _);
    }

    private static string Normalize(string mac) =>
        mac.Replace(":", "").Replace("-", "").ToLowerInvariant();

    // The default site is spelled both as its slug and as an empty string across the registries,
    // and a writer disagreeing with a reader here would silently disable suppression.
    private static string NormalizeSite(string? siteSlug) =>
        string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
}
