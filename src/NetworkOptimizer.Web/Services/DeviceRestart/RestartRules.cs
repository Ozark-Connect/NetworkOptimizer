using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Firmware;

namespace NetworkOptimizer.Web.Services.DeviceRestart;

/// <summary>Site-wide facts the lockout reads besides the device tree.</summary>
/// <param name="Rollout">The site's Firmware Rollout to surface, or null.</param>
/// <param name="ChangeControlApplying">True while a Change Control plan is writing or in its commit-confirmed window.</param>
public sealed record RestartSiteState(SiteRolloutStatus? Rollout, bool ChangeControlApplying);

/// <summary>A PoE port that powers a device.</summary>
/// <param name="SwitchMac">The switch that powers it.</param>
/// <param name="SwitchName">The switch's name, for the sheet and the reboot reason.</param>
/// <param name="PortIdx">The port's <c>port_idx</c>.</param>
public sealed record PoePort(string SwitchMac, string SwitchName, int PortIdx);

/// <summary>
/// The restart rules that need no I/O: the lockout, PoE eligibility, the once-each check, and
/// which open request the executor works on next.
/// </summary>
public static class RestartRules
{
    /// <summary>How soon a scheduled Firmware Rollout has to start to lock restarts out.</summary>
    public static readonly TimeSpan RolloutLeadLockout = TimeSpan.FromMinutes(60);

    /// <summary>A device that booted this long before its due time is not restarted again.</summary>
    public static readonly TimeSpan CoalescingWindow = TimeSpan.FromMinutes(15);

    /// <summary>How long the executor waits for a device and its path to come Online before giving up on reaching it.</summary>
    public static readonly TimeSpan OnlineWait = TimeSpan.FromMinutes(20);

    /// <summary>Time between command attempts.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

    /// <summary>Command attempts before the PoE fallback, or failure.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Wait after a lockout clears before a held restart runs.</summary>
    public static readonly TimeSpan LockoutSettle = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a restarted device gets to go down and come back. A gateway takes the console's
    /// own link with it on a UniFi OS console, so it gets longer.
    /// </summary>
    public static TimeSpan OfflineBudget(DeviceType type) =>
        type == DeviceType.Gateway ? TimeSpan.FromMinutes(15) : TimeSpan.FromMinutes(10);

    /// <summary>
    /// Why the device may not restart now, or null when it may. Covers the device, everything above
    /// it, and everything below it: a switch restart cuts PoE to an AP that may be mid-flash.
    /// </summary>
    public static string? Lockout(RestartTopology topology, string mac, RestartSiteState site, DateTime nowUtc)
    {
        // Upgrading, or about to. A paused or soaking rollout is not flashing anything, and it can sit
        // there for days; a device that is mid-flash is caught by its own state below.
        if (site.Rollout is { } rollout)
        {
            if (rollout.Stage == SiteRolloutStage.Running)
                return "A Firmware Rollout is in progress on this site.";
            if (rollout.Stage == SiteRolloutStage.Scheduled && rollout.At is { } start && start - nowUtc <= RolloutLeadLockout)
                return start > nowUtc
                    ? $"A Firmware Rollout starts in {TimeFormatHelper.FormatDuration(Max(start - nowUtc, TimeSpan.FromMinutes(1)))}."
                    : "A Firmware Rollout is about to start.";
        }

        if (site.ChangeControlApplying)
            return "A Change Control plan is being applied on this site.";

        var device = topology.Find(mac);
        if (device == null) return null;

        foreach (var node in topology.PathToRoot(mac).Prepend(device).Concat(topology.Subtree(mac)))
        {
            if (!node.IsBusy) continue;
            var what = node.State == RestartNode.UpgradingState ? "upgrading its firmware" : "provisioning";
            return $"{node.Name} is {what}.";
        }
        return null;
    }

    /// <summary>
    /// The PoE port that powers the device, or null when it is not powered from a known switch.
    /// <paramref name="lastWatts"/> is the port's draw on the last reading taken while the device
    /// was up: an offline device's port can read 0 W, and that is the case the power-cycle is for.
    /// </summary>
    public static PoePort? PoweringPort(RestartTopology topology, string mac, double? lastWatts)
    {
        var device = topology.Find(mac);
        if (device is not { UplinkPortIdx: int port } || port < 1) return null;
        // A switch, or a gateway with PoE ports (UCG Max, UDR7): what counts is that its port delivers power.
        if (topology.Parent(device) is not { } parent || !parent.IsOnline) return null;

        var watts = parent.PoeWatts != null && parent.PoeWatts.TryGetValue(port, out var now) ? now : 0;
        if (watts <= 0 && lastWatts is not > 0) return null;
        return new PoePort(parent.Mac, parent.Name, port);
    }

    /// <summary>
    /// When the device last booted, from the console's uptime, or null when the console reports none.
    /// </summary>
    public static DateTime? BootedAt(RestartNode node, DateTime readAtUtc) =>
        node.UptimeSeconds > 0 ? readAtUtc - TimeSpan.FromSeconds(node.UptimeSeconds) : null;

    /// <summary>
    /// True when the device has already restarted for this request: it booted after the request
    /// was made and within <see cref="CoalescingWindow"/> of its due time. An upstream restart that
    /// cut its power, or a restart of its own, then counts, and it is not restarted twice.
    /// </summary>
    public static bool AlreadyRestarted(DeviceRestartRequest request, RestartNode node, DateTime readAtUtc)
    {
        if (BootedAt(node, readAtUtc) is not { } booted) return false;
        var since = Max(request.CreatedAt, request.DueAtUtc - CoalescingWindow);
        return booted >= since && node.IsOnline;
    }

    /// <summary>
    /// The open request the executor works on next: the highest one in the tree (root first),
    /// then the earliest due. Null when nothing is due yet. Requests already started keep the turn,
    /// so two devices are never in flight at once.
    /// </summary>
    public static DeviceRestartRequest? Next(IReadOnlyList<DeviceRestartRequest> open, RestartTopology topology, DateTime nowUtc) =>
        Candidates(open, topology, nowUtc).FirstOrDefault();

    /// <summary>
    /// The open requests the executor may work on, in order. A commanded request is the only one
    /// until it settles. Otherwise every due request, root first, except one below a device whose
    /// own request is already under way or held: the tree is taken from the top.
    /// </summary>
    public static IReadOnlyList<DeviceRestartRequest> Candidates(IReadOnlyList<DeviceRestartRequest> open, RestartTopology topology, DateTime nowUtc)
    {
        var started = open.FirstOrDefault(r => r.Status == DeviceRestartStatus.Running && r.LastCommandAt != null);
        if (started != null) return [started];

        var underWay = open.Where(r => r.Status is DeviceRestartStatus.Running or DeviceRestartStatus.Waiting).ToList();
        return open
            .Where(r => r.IsOpen && r.DueAtUtc <= nowUtc)
            .Where(r => !underWay.Any(u => u != r && topology.IsAbove(u.DeviceMac, r.DeviceMac)))
            .OrderBy(r => topology.Depth(r.DeviceMac))
            .ThenBy(r => r.DueAtUtc)
            .ThenBy(r => r.Id)
            .ToList();
    }

    /// <summary>
    /// The upstream device among <paramref name="recent"/> that restarted and explains this one's
    /// boot, for the reboot reason; null when none did.
    /// </summary>
    public static DeviceRestartRequest? UpstreamCause(RestartTopology topology, string mac, IEnumerable<DeviceRestartRequest> recent) =>
        recent
            .Where(r => r.Status == DeviceRestartStatus.Done && topology.IsAbove(r.DeviceMac, mac))
            .OrderByDescending(r => r.CompletedAt)
            .FirstOrDefault();

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
