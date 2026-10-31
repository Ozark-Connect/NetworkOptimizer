using System.Collections.Concurrent;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Publishes <c>device.offline</c> and <c>device.recovered</c> from what UniFi Network reports for
/// each device, as a paired offline/recovery cycle like the agent and monitoring-target alerts.
///
/// Crucially it stays silent for a restart that was asked for. UniFi distinguishes a device that
/// dropped from one that is upgrading, provisioning or being adopted - those map to
/// <see cref="DeviceStatusKind.Transitional"/> - so a firmware run does not read as an outage.
/// A device already announced offline that turns out to be upgrading is treated as recovered, since
/// the reason it stopped answering is now known and benign.
///
/// "Disconnected" in UniFi Network only means the device stopped informing, which a Network restart
/// or an inform address change causes with no traffic lost. Before announcing an outage the device
/// is checked on the data plane (its fabric target, else a ping started on the first offline
/// observation, so the announcement is no later than without it). A device that still answers is
/// management-only: held for <see cref="UnmanagedHold"/>, then <c>device.unmanaged</c>, or one
/// <c>console.devices_unmanaged</c> when most of the site is out together.
/// </summary>
public class DeviceStateAlertEvaluator
{
    /// <summary>Event type published when a device stops being reachable for reasons unknown.</summary>
    public const string OfflineEventType = "device.offline";

    /// <summary>Event type published when it comes back.</summary>
    public const string RecoveredEventType = "device.recovered";

    /// <summary>A device that answers on the network but has not reported to UniFi Network for <see cref="UnmanagedHold"/>.</summary>
    public const string UnmanagedEventType = "device.unmanaged";

    /// <summary>It reports to UniFi Network again. Closes only <see cref="UnmanagedEventType"/>, never an outage.</summary>
    public const string ManagedEventType = "device.managed";

    /// <summary>Most of the site is management-only at once: one alert for the console side instead of one per device.</summary>
    public const string GroupUnmanagedEventType = "console.devices_unmanaged";

    /// <summary>Closes <see cref="GroupUnmanagedEventType"/>.</summary>
    public const string GroupManagedEventType = "console.devices_managed";

    /// <summary>DeviceId of the site-wide pair, so it is never mistaken for one device's.</summary>
    public const string GroupDeviceId = "all-devices";

    /// <summary>
    /// How long a device may answer on the network without reporting to UniFi Network before it is
    /// alerted on. Nothing is down in that state, so speed does not matter; a Network restart or an
    /// inform address change clears well inside it.
    /// </summary>
    public static readonly TimeSpan UnmanagedHold = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Consecutive offline observations before announcing it. UniFi can report a single offline
    /// sample during a config apply or a brief adoption blip, and the poll cadence means two
    /// samples still announce within about a minute.
    /// </summary>
    private const int OfflineObservationsToAnnounce = 2;

    // A fabric result older than this says nothing about the device now.
    private static readonly TimeSpan FabricFreshness = TimeSpan.FromSeconds(30);
    // The ping normally finished a poll ago; this bounds the wait when it has not.
    private static readonly TimeSpan ProbeWait = TimeSpan.FromSeconds(5);
    // Devices seen this recently make up the site. Well under the hold, so a device removed from the
    // console while held drops out before its hold expires.
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(2);
    private const int CollapseMinimum = 3;

    private readonly IAlertEventBus _eventBus;
    private readonly DeviceTransitionTracker _transitions;
    private readonly DeviceOfflineDeduplicator _dedup;
    private readonly Firmware.RolloutSuppressionRegistry? _rolloutWindows;
    private readonly ILogger<DeviceStateAlertEvaluator> _logger;
    private readonly ConcurrentDictionary<string, DeviceAlertState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _siteSlug;
    private readonly string _siteSuffix;
    private readonly IDeviceReachabilityProbe? _reachability;
    private readonly MonitoringAlertEvaluator? _fabric;
    private readonly OpenAlertSeed _openAtStart;
    private OpenAlerts _open = OpenAlerts.None;
    private HashSet<string> _groupMembersAtStart = [];
    private bool _seedApplied;
    private bool _groupOpen;

    /// <param name="eventBus">Site-stamped alert bus.</param>
    /// <param name="transitions">Which devices UniFi reports as mid-transition.</param>
    /// <param name="dedup">Shared de-dup tracker for device offline/recovered alerts.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="siteSlug">Site this instance evaluates for (one per site, owned by the registry).</param>
    /// <param name="rolloutWindows">
    /// Devices inside their own firmware rollout window. Separate from <paramref name="transitions"/>
    /// because UniFi flips a device to plain Offline partway through some upgrades, which is exactly
    /// the window where its own report cannot be relied on.
    /// </param>
    /// <param name="scopeFactory">
    /// Reads the site's open alerts once, so a condition that began before a restart still gets its
    /// recovery. Without it, state starts empty.
    /// </param>
    /// <param name="reachability">
    /// Data-plane check for a device UniFi reports disconnected. Without it every disconnect is
    /// announced as an outage, as before it existed.
    /// </param>
    /// <param name="fabric">The site's target evaluator, whose fabric results answer for a device without a ping.</param>
    public DeviceStateAlertEvaluator(
        IAlertEventBus eventBus,
        DeviceTransitionTracker transitions,
        DeviceOfflineDeduplicator dedup,
        ILogger<DeviceStateAlertEvaluator> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug,
        Firmware.RolloutSuppressionRegistry? rolloutWindows = null,
        IServiceScopeFactory? scopeFactory = null,
        IDeviceReachabilityProbe? reachability = null,
        MonitoringAlertEvaluator? fabric = null)
    {
        _reachability = reachability;
        _fabric = fabric;
        _eventBus = eventBus;
        _transitions = transitions;
        _dedup = dedup;
        _rolloutWindows = rolloutWindows;
        _logger = logger;
        _siteSlug = siteSlug ?? SiteManagementService.DefaultSiteSlug;
        _siteSuffix = string.IsNullOrEmpty(siteSlug) || siteSlug == SiteManagementService.DefaultSiteSlug
            ? "" : $" (site {siteSlug})";
        _openAtStart = new OpenAlertSeed(scopeFactory, _siteSlug,
            [OfflineEventType, UnmanagedEventType, GroupUnmanagedEventType], SeedSubject, logger);
    }

    private sealed class DeviceAlertState
    {
        public required string Mac;
        public string Label = "";
        public string? Name;
        public string? Ip;
        public DeviceType Type;
        public DateTime LastSeen;

        public int ConsecutiveOffline;
        public DateTime FirstOfflineAt;

        /// <summary>device.offline published, or open at start-up.</summary>
        public bool Announced;

        /// <summary>Answering on the network while UniFi reports it disconnected: held, published, or grouped.</summary>
        public DateTime? UnmanagedSince;

        /// <summary>device.unmanaged published, or open at start-up.</summary>
        public bool UnmanagedPublished;

        /// <summary>Covered by the site's open console.devices_unmanaged.</summary>
        public bool InGroup;

        /// <summary>The reachability check in flight, read on the next observation.</summary>
        public Task<DeviceReach>? Probe;
    }

    /// <summary>
    /// Feed one device's reported state.
    /// </summary>
    /// <param name="deviceMac">Device MAC.</param>
    /// <param name="deviceName">Device name for the alert text.</param>
    /// <param name="deviceIp">Device address for the alert payload, and what the reachability check pings.</param>
    /// <param name="deviceType">Device type, for the alert context.</param>
    /// <param name="unifiState">The device's UniFi <c>state</c> value.</param>
    /// <param name="now">Current time.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask EvaluateAsync(
        string deviceMac,
        string? deviceName,
        string? deviceIp,
        DeviceType deviceType,
        int unifiState,
        DateTime now,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceMac)) return;

        await EnsureSeededAsync();
        var status = UniFiDeviceStateMap.ToStatus(unifiState);
        var state = _states.GetOrAdd(deviceMac, NewState);
        var label = string.IsNullOrWhiteSpace(deviceName) ? deviceMac : deviceName;
        state.Label = label;
        state.Name = deviceName;
        state.Ip = deviceIp;
        state.Type = deviceType;
        state.LastSeen = now;

        // Upgrading, provisioning, adopting: someone or something asked for this. Never announce,
        // and close out an announcement already made - the silence now has a known cause.
        if (status.Kind == DeviceStatusKind.Transitional)
        {
            state.ConsecutiveOffline = 0;
            if (state.Announced)
            {
                state.Announced = false;
                await PublishRecoveredAsync(deviceMac, label, deviceIp, deviceType,
                    $"{label} is {status.Label.ToLowerInvariant()}, so its outage was an expected restart.", ct);
            }
            // A transitional state is reported through inform, so UniFi Network hears it again.
            await ClearUnmanagedAsync(state, ct);

            _logger.LogDebug(
                "Not alerting on {Device} ({Mac}): UniFi reports it {Status}, which is an initiated change",
                label, deviceMac, status.Label);
            return;
        }

        if (status.Kind == DeviceStatusKind.Offline)
        {
            // A transition observed moments ago still counts, because UniFi flips a device to
            // Offline partway through some upgrades before the new firmware checks back in.
            if (_transitions.IsInKnownTransition(_siteSlug, deviceMac, now))
            {
                state.ConsecutiveOffline = 0;
                _logger.LogDebug(
                    "Not alerting on {Device} ({Mac}) going offline: it was mid-transition moments ago",
                    label, deviceMac);
                return;
            }

            if (_rolloutWindows?.IsInRolloutWindow(_siteSlug, deviceMac, now) == true)
            {
                state.ConsecutiveOffline = 0;
                _logger.LogDebug(
                    "Not alerting on {Device} ({Mac}) going offline: a firmware rollout is upgrading it or a device between it and the console, or a zone change is re-provisioning the gateway",
                    label, deviceMac);
                return;
            }

            if (state.ConsecutiveOffline++ == 0) state.FirstOfflineAt = now;
            if (state.Announced) return;

            // Started on the first observation, so the result is in hand by the threshold.
            var reach = ReadReach(state, now);

            if (state.UnmanagedSince is { } since)
            {
                // Held or reported as management-only: traffic stopping is an outage, reported at once.
                if (reach == DeviceReach.Silent)
                    await AnnounceOfflineAsync(state, unifiState, since, ct);
                return;
            }

            if (state.ConsecutiveOffline < OfflineObservationsToAnnounce)
                return;

            if (reach == null && state.Probe is { } inFlight)
            {
                await Task.WhenAny(inFlight, Task.Delay(ProbeWait, ct));
                reach = ReadReach(state, now);
            }

            if (reach == DeviceReach.Answered)
            {
                state.UnmanagedSince = state.FirstOfflineAt;
                _logger.LogInformation(
                    "{Device} ({Mac}) is not reporting to UniFi Network but answers on the network; holding as management-only",
                    label, deviceMac);
                return;
            }

            await AnnounceOfflineAsync(state, unifiState, unmanagedSince: null, ct);
            return;
        }

        // Online (or any other non-offline, non-transitional state): clear and close the cycle.
        state.ConsecutiveOffline = 0;
        state.Probe = null;
        if (state.Announced)
        {
            state.Announced = false;
            await PublishRecoveredAsync(deviceMac, label, deviceIp, deviceType,
                $"{label} is reporting to UniFi Network again.", ct);
        }
        await ClearUnmanagedAsync(state, ct);
    }

    /// <summary>
    /// Run after every device of a pass has been fed. Decides, with the whole site in view, which
    /// held devices are alerted on one by one and when they are one site-wide alert.
    /// </summary>
    /// <param name="now">Current time.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask CompletePassAsync(DateTime now, CancellationToken ct = default)
    {
        await EnsureSeededAsync();
        // A device no longer in the console's list (removed while held) is never fed again; it must
        // neither be alerted on nor hold a group open.
        var states = _states.Values.Where(s => now - s.LastSeen <= ActiveWindow).ToList();
        var collapseAt = CollapseAt(states.Count);

        if (_groupOpen)
        {
            // Later arrivals join the open group silently: it already says UniFi isn't hearing from them.
            foreach (var s in states.Where(s => s.UnmanagedSince != null && !s.UnmanagedPublished && !s.Announced))
                s.InGroup = true;

            var stillOut = states.Where(s => s.InGroup).ToList();
            if (stillOut.Count >= collapseAt) return;

            await PublishGroupManagedAsync(stillOut.Count, ct);
            _groupOpen = false;
            foreach (var s in _states.Values) s.InGroup = false;
            // The few still out fall through and are alerted on one by one.
        }

        var held = states.Where(s => s.UnmanagedSince != null && !s.UnmanagedPublished && !s.InGroup && !s.Announced).ToList();
        var due = held.Where(s => now - s.UnmanagedSince!.Value >= UnmanagedHold).ToList();
        if (due.Count == 0) return;

        if (held.Count >= collapseAt)
        {
            await PublishGroupUnmanagedAsync(held, now, ct);
            foreach (var s in held) s.InGroup = true;
            _groupOpen = true;
            return;
        }

        foreach (var s in due)
        {
            await PublishUnmanagedAsync(s, now, ct);
            s.UnmanagedPublished = true;
        }
    }

    /// <summary>The share of the site that makes a management-only drop the console's, not the devices'.</summary>
    internal static int CollapseAt(int activeDevices) =>
        Math.Max(CollapseMinimum, (int)Math.Ceiling(activeDevices / 2.0));

    /// <summary>A device's alerts key on its MAC; the site-wide pair on <see cref="GroupDeviceId"/> as stored.</summary>
    private static string? SeedSubject(NetworkOptimizer.Alerts.Models.AlertHistoryEntry alert) =>
        alert.EventType is GroupUnmanagedEventType or GroupManagedEventType ? alert.DeviceId : OpenAlertSeed.MacSubject(alert.DeviceId);

    /// <summary>A device first seen by this process, carrying any of its conditions open at start-up.</summary>
    private DeviceAlertState NewState(string mac)
    {
        var key = OpenAlertSeed.MacSubject(mac);
        var state = new DeviceAlertState { Mac = mac, Announced = _open.Take(OfflineEventType, key) != null };
        if (_open.Take(UnmanagedEventType, key) is { } unmanaged)
        {
            state.UnmanagedSince = OpenAlertSeed.ContextTime(unmanaged, "unmanaged_since");
            state.UnmanagedPublished = true;
        }
        if (key != null && _groupMembersAtStart.Contains(key))
        {
            state.InGroup = true;
            state.UnmanagedSince ??= _open.Get(GroupUnmanagedEventType, GroupDeviceId) is { } group
                ? OpenAlertSeed.ContextTime(group, "unmanaged_since")
                : DateTime.UtcNow;
        }
        return state;
    }

    /// <summary>
    /// The freshest data-plane answer for the device, or null while a ping is still running. A
    /// finished ping is consumed and the next one started, so a held device is re-checked every poll.
    /// </summary>
    private DeviceReach? ReadReach(DeviceAlertState state, DateTime now)
    {
        if (_fabric?.FabricReachable(state.Mac, FabricFreshness, now) is { } up)
        {
            var fabric = up ? DeviceReach.Answered : DeviceReach.Silent;
            _logger.LogDebug("{Device} ({Mac}) reachability from its fabric target: {Reach}", state.Label, state.Mac, fabric);
            return fabric;
        }

        if (state.Probe is { IsCompleted: true } done)
        {
            state.Probe = null;
            var result = done.IsCompletedSuccessfully ? done.Result : DeviceReach.CouldNotRun;
            _logger.LogDebug("{Device} ({Mac}) reachability from a ping of {Ip}: {Reach}", state.Label, state.Mac, state.Ip, result);
            StartProbe(state);
            return result;
        }

        if (state.Probe == null) StartProbe(state);
        return null;
    }

    private void StartProbe(DeviceAlertState state) =>
        // Not tied to the caller's token: an agent batch's token ends with the batch, long before the read.
        state.Probe = _reachability == null || string.IsNullOrWhiteSpace(state.Ip)
            ? Task.FromResult(DeviceReach.CouldNotRun)
            : _reachability.ProbeAsync(_siteSlug, state.Ip, CancellationToken.None);

    private async ValueTask AnnounceOfflineAsync(DeviceAlertState state, int unifiState, DateTime? unmanagedSince, CancellationToken ct)
    {
        state.Announced = true;
        state.Probe = null;

        if (!_dedup.TryClaimSlot(state.Mac, isRecovery: false, DateTime.UtcNow))
        {
            _logger.LogDebug(
                "Suppressing device.offline for {Device} ({Mac}): monitoring target_offline already fired",
                state.Label, state.Mac);
            return;
        }

        var context = new Dictionary<string, string>
        {
            ["device_type"] = state.Type.ToString(),
            ["unifi_state"] = unifiState.ToString()
        };
        if (unmanagedSince is { } since) context["unmanaged_since"] = since.ToString("O");

        await _eventBus.PublishAsync(new AlertEvent
        {
            EventType = OfflineEventType,
            Source = "device",
            Severity = AlertSeverity.Error,
            Title = $"Device Offline: {state.Label}{_siteSuffix}",
            Message = unmanagedSince == null
                ? $"{state.Label} stopped reporting to UniFi Network and is not upgrading or provisioning."
                : $"{state.Label} had stopped reporting to UniFi Network and has now stopped answering on the network too.",
            DeviceId = state.Mac,
            DeviceName = state.Name,
            DeviceIp = state.Ip,
            SourceUrl = MonitoringLinks.DeviceStats(state.Mac, MonitoringLinks.NowMs()),
            Context = context
        }, ct);

        _logger.LogInformation("Published device.offline for {Device} ({Mac})", state.Label, state.Mac);
    }

    /// <summary>UniFi Network hears the device again: close its management-only state.</summary>
    private async ValueTask ClearUnmanagedAsync(DeviceAlertState state, CancellationToken ct)
    {
        if (state.UnmanagedPublished)
        {
            await _eventBus.PublishAsync(new AlertEvent
            {
                EventType = ManagedEventType,
                Source = "device",
                Severity = AlertSeverity.Info,
                Title = $"Reporting to UniFi Network Again: {state.Label}{_siteSuffix}",
                Message = $"{state.Label} is reporting to UniFi Network again.",
                DeviceId = state.Mac,
                DeviceName = state.Name,
                DeviceIp = state.Ip,
                SourceUrl = MonitoringLinks.DeviceStats(state.Mac, MonitoringLinks.NowMs()),
                Context = new Dictionary<string, string> { ["device_type"] = state.Type.ToString() }
            }, ct);
            _logger.LogInformation("Published device.managed for {Device} ({Mac})", state.Label, state.Mac);
        }
        state.UnmanagedSince = null;
        state.UnmanagedPublished = false;
        state.InGroup = false;
    }

    private async ValueTask PublishUnmanagedAsync(DeviceAlertState state, DateTime now, CancellationToken ct)
    {
        var since = state.UnmanagedSince!.Value;
        await _eventBus.PublishAsync(new AlertEvent
        {
            EventType = UnmanagedEventType,
            Source = "device",
            Severity = AlertSeverity.Warning,
            Title = $"Not Reporting to UniFi Network: {state.Label}{_siteSuffix}",
            Message = $"{state.Label} has not reported to UniFi Network for {Minutes(now - since)} minutes but still answers on the network, " +
                      "so its traffic is not affected. Check its inform address and the UniFi Network application.",
            DeviceId = state.Mac,
            DeviceName = state.Name,
            DeviceIp = state.Ip,
            SourceUrl = MonitoringLinks.DeviceStats(state.Mac, MonitoringLinks.NowMs()),
            Context = new Dictionary<string, string>
            {
                ["device_type"] = state.Type.ToString(),
                ["unmanaged_since"] = since.ToString("O")
            }
        }, ct);
        _logger.LogInformation("Published device.unmanaged for {Device} ({Mac})", state.Label, state.Mac);
    }

    private async ValueTask PublishGroupUnmanagedAsync(List<DeviceAlertState> held, DateTime now, CancellationToken ct)
    {
        var since = held.Min(s => s.UnmanagedSince!.Value);
        await _eventBus.PublishAsync(new AlertEvent
        {
            EventType = GroupUnmanagedEventType,
            Source = "console",
            Severity = AlertSeverity.Warning,
            Title = $"UniFi Network isn't hearing from {held.Count} devices{_siteSuffix}",
            Message = $"{held.Count} devices have not reported to UniFi Network for {Minutes(now - since)} minutes but still answer on the network, " +
                      "so their traffic is not affected. This usually means UniFi Network restarted or the devices' inform address changed.",
            DeviceId = GroupDeviceId,
            Context = new Dictionary<string, string>
            {
                ["devices"] = string.Join(",", held.Select(s => s.Mac).Order(StringComparer.OrdinalIgnoreCase)),
                ["unmanaged_since"] = since.ToString("O")
            }
        }, ct);
        _logger.LogInformation("Published console.devices_unmanaged for {Count} devices", held.Count);
    }

    private async ValueTask PublishGroupManagedAsync(int stillOut, CancellationToken ct)
    {
        await _eventBus.PublishAsync(new AlertEvent
        {
            EventType = GroupManagedEventType,
            Source = "console",
            Severity = AlertSeverity.Info,
            Title = $"UniFi Network Is Hearing From Devices Again{_siteSuffix}",
            Message = stillOut == 0
                ? "Every device is reporting to UniFi Network again."
                : $"Most devices are reporting to UniFi Network again. {stillOut} still are not and are alerted on their own.",
            DeviceId = GroupDeviceId,
        }, ct);
        _logger.LogInformation("Published console.devices_managed ({StillOut} still out)", stillOut);
    }

    private static int Minutes(TimeSpan span) => Math.Max(1, (int)Math.Round(span.TotalMinutes));

    /// <summary>Applies the conditions open at start-up, once, before the first state is created.</summary>
    private async ValueTask EnsureSeededAsync()
    {
        if (_seedApplied) return;
        var open = await _openAtStart.OpenAsync();
        if (_seedApplied) return;
        _open = open;
        if (open.Get(GroupUnmanagedEventType, GroupDeviceId) is { } group)
        {
            _groupOpen = true;
            _groupMembersAtStart = (OpenAlertSeed.Context(group, "devices") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(DeviceOfflineDeduplicator.NormalizeMac)
                .ToHashSet();
        }
        _seedApplied = true;
    }

    private async ValueTask PublishRecoveredAsync(
        string deviceMac, string label, string? deviceIp, DeviceType deviceType, string message, CancellationToken ct)
    {
        if (!_dedup.TryClaimSlot(deviceMac, isRecovery: true, DateTime.UtcNow))
        {
            _logger.LogDebug(
                "Suppressing device.recovered for {Device} ({Mac}): monitoring target_recovered already fired",
                label, deviceMac);
            return;
        }

        await _eventBus.PublishAsync(new AlertEvent
        {
            EventType = RecoveredEventType,
            Source = "device",
            Severity = AlertSeverity.Info,
            Title = $"Device Recovered: {label}{_siteSuffix}",
            Message = message,
            DeviceId = deviceMac,
            DeviceName = label,
            DeviceIp = deviceIp,
            SourceUrl = MonitoringLinks.DeviceStats(deviceMac, MonitoringLinks.NowMs()),
            Context = new Dictionary<string, string> { ["device_type"] = deviceType.ToString() }
        }, ct);

        _logger.LogInformation("Published device.recovered for {Device} ({Mac})", label, deviceMac);
    }
}
