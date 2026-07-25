using System.Collections.Concurrent;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Remembers which devices UniFi Network currently reports as mid-transition - upgrading,
/// provisioning, adopting, pending or being deleted.
///
/// A device that is installing firmware or being provisioned stops answering probes for a minute or
/// two, and alerting on that is noise: the restart was asked for. UniFi already tells us, since
/// those states map to <see cref="DeviceStatusKind.Transitional"/>, so the offline path can simply
/// check here before declaring anything wrong.
///
/// Keyed by site as well as MAC: MACs are unique but a slug-less key would let one site's device
/// state silence another site's alerts. Entries go stale on purpose - if device state stops being
/// observed (console down, collection stopped) suppression lapses and alerts resume, because
/// failing to alert is worse than alerting during an upgrade.
/// </summary>
public class DeviceTransitionTracker
{
    /// <summary>
    /// How long a transitional observation keeps suppressing. Long enough to cover a firmware
    /// install between poll cycles, short enough that a forgotten entry cannot mute a real outage.
    /// </summary>
    public static readonly TimeSpan ObservationFreshness = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<(string Site, string Mac), DateTime> _transitionalAt = new();

    /// <summary>
    /// Record what UniFi Network reports for a device right now. A non-transitional state clears
    /// any suppression immediately, so a device that finishes upgrading is alertable again at once.
    /// </summary>
    /// <param name="siteSlug">Site the device belongs to.</param>
    /// <param name="deviceMac">Device MAC.</param>
    /// <param name="unifiState">The device's UniFi <c>state</c> value.</param>
    /// <param name="observedAt">When this state was observed.</param>
    public void Record(string siteSlug, string? deviceMac, int unifiState, DateTime observedAt)
    {
        if (string.IsNullOrWhiteSpace(deviceMac)) return;

        var key = (NormalizeSite(siteSlug), Normalize(deviceMac));
        if (UniFiDeviceStateMap.ToStatus(unifiState).Kind == DeviceStatusKind.Transitional)
            _transitionalAt[key] = observedAt.ToUniversalTime();
        else
            _transitionalAt.TryRemove(key, out _);
    }

    /// <summary>
    /// Whether this device is in a change someone or something initiated, as of a recent enough
    /// observation to trust. False when nothing has been observed lately, so alerting wins by default.
    /// </summary>
    /// <param name="siteSlug">Site the device belongs to.</param>
    /// <param name="deviceMac">Device MAC.</param>
    /// <param name="now">Current time.</param>
    public bool IsInKnownTransition(string siteSlug, string? deviceMac, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(deviceMac)) return false;

        if (!_transitionalAt.TryGetValue((NormalizeSite(siteSlug), Normalize(deviceMac)), out var at))
            return false;

        return now.ToUniversalTime() - at <= ObservationFreshness;
    }

    private static string Normalize(string mac) =>
        mac.Replace(":", "").Replace("-", "").ToLowerInvariant();

    // The default site is spelled both as its slug and as an empty string across the registries,
    // and a writer disagreeing with a reader here would silently disable suppression.
    private static string NormalizeSite(string? siteSlug) =>
        string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
}
