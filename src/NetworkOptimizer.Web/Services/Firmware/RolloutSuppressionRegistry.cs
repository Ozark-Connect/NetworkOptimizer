using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// Remembers which devices are inside their own firmware rollout window, so the standard
/// <c>device.offline</c> and <c>device.rebooted</c> alerts stay quiet for a restart the rollout
/// asked for. The rollout's own alerts always flow; this only mutes the generic ones, and only
/// while the plan has <c>SuppressStandardAlerts</c> on.
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
    /// Ends a device's window at once, so a settled step is alertable again without waiting out
    /// the staleness.
    /// </summary>
    /// <param name="siteSlug">Site the device belongs to.</param>
    /// <param name="deviceMac">Device MAC in any format.</param>
    public void Clear(string siteSlug, string? deviceMac)
    {
        if (string.IsNullOrWhiteSpace(deviceMac)) return;
        _windowRefreshedAt.TryRemove((NormalizeSite(siteSlug), Normalize(deviceMac)), out _);
    }

    /// <summary>
    /// Whether this device is inside a rollout window that was confirmed recently enough to trust.
    /// </summary>
    /// <param name="siteSlug">Site the device belongs to.</param>
    /// <param name="deviceMac">Device MAC in any format.</param>
    /// <param name="now">Current time.</param>
    public bool IsInRolloutWindow(string siteSlug, string? deviceMac, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(deviceMac)) return false;

        if (!_windowRefreshedAt.TryGetValue((NormalizeSite(siteSlug), Normalize(deviceMac)), out var at))
            return false;

        return now.ToUniversalTime() - at <= WindowFreshness;
    }

    /// <summary>Drops every window a site holds (rollout finished, aborted, or the site went away).</summary>
    /// <param name="siteSlug">Site to clear.</param>
    public void ClearSite(string siteSlug)
    {
        var site = NormalizeSite(siteSlug);
        foreach (var key in _windowRefreshedAt.Keys.Where(k => k.Site == site).ToList())
            _windowRefreshedAt.TryRemove(key, out _);
    }

    private static string Normalize(string mac) =>
        mac.Replace(":", "").Replace("-", "").ToLowerInvariant();

    // The default site is spelled both as its slug and as an empty string across the registries,
    // and a writer disagreeing with a reader here would silently disable suppression.
    private static string NormalizeSite(string? siteSlug) =>
        string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
}
