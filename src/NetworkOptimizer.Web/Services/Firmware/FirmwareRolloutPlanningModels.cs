using System.Text.Json;
using System.Text.Json.Serialization;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// Timing and offline-budget class a device falls into for upgrade estimates.
/// </summary>
public enum FirmwareDeviceClass
{
    /// <summary>Modern AP (U6/U7 era): short, tight reboot window.</summary>
    AccessPoint,
    /// <summary>Older AP generation (UAP/AC era): consistently slower cycles.</summary>
    OlderAccessPoint,
    /// <summary>Switch: ~8 minute cycles across SKUs.</summary>
    Switch,
    /// <summary>Network-firmware-only gateway (UXG class): comparable to switches.</summary>
    GatewayNetworkOnly,
    /// <summary>Cloud Gateway running UniFi OS (UDM/UDR/UCG): slowest, console down during the cycle.</summary>
    CloudGatewayUniFiOs
}

/// <summary>
/// Point-in-time snapshot of one device as the planner sees it. MACs are normalized
/// lowercase-with-colons; depth is frozen at plan time because mesh reparenting makes
/// the live topology dynamic.
/// </summary>
public class PlannerDevice
{
    /// <summary>Normalized (lowercase, colon-separated) device MAC.</summary>
    public required string Mac { get; init; }

    /// <summary>Display name; falls back to the friendly model name upstream.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Raw SKU code (e.g. U7PRO) - the canary and timing-store key.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>Friendly model name (e.g. "U7 Pro") for display and generation heuristics.</summary>
    public string DisplayModel { get; init; } = string.Empty;

    /// <summary>Effective device type (UniFi re-classification already applied).</summary>
    public DeviceType Type { get; init; }

    /// <summary>Whether the console offers an upgrade for this device.</summary>
    public bool Upgradable { get; init; }

    /// <summary>Firmware version currently running.</summary>
    public string? FromVersion { get; init; }

    /// <summary>Target version the console reports (UpgradeToFirmware).</summary>
    public string? ToVersion { get; init; }

    /// <summary>Normalized parent MAC, null for the root or when unknown.</summary>
    public string? UplinkMac { get; init; }

    /// <summary>Local port index of this device's own uplink (wired only).</summary>
    public int? UplinkLocalPort { get; init; }

    /// <summary>Remote port index on the parent that bounds this device.</summary>
    public int? UplinkRemotePort { get; init; }

    /// <summary>True when the uplink is a wireless mesh backhaul.</summary>
    public bool WirelessUplink { get; init; }

    /// <summary>Mesh STA interface (vwiresta*) when this is a mesh child, else null.</summary>
    public string? MeshUplinkInterface { get; init; }

    /// <summary>Device IP for SSH-path commands and mesh re-pair.</summary>
    public string? IpAddress { get; init; }
}

/// <summary>
/// Answers "would taking these two APs down together strand roaming clients".
/// Built from AP placements (propagation) corroborated by UniFi roaming edges;
/// a null oracle means no placement or roaming data exists (uniform-density fallback).
/// </summary>
public interface IApNeighborOracle
{
    /// <summary>True when the two APs overlap (interfere) or share a real roaming edge.</summary>
    bool AreNeighbors(string macA, string macB);

    /// <summary>Whether real placement data backed this oracle (surfaces in plan notes).</summary>
    bool HasPlacementData { get; }
}

/// <summary>
/// Everything the planner consumes, frozen at plan time. Pure input - no live services.
/// </summary>
public class RolloutPlanningInput
{
    public required IReadOnlyList<PlannerDevice> Devices { get; init; }
    public required FirmwareRolloutSettings Settings { get; init; }
    public required FirmwareTimingEstimator Estimator { get; init; }

    /// <summary>The console's current device firmware channel (super_fwupdate.firmware_channel).</summary>
    public string CurrentConsoleChannel { get; init; } = FirmwareChannels.Release;

    /// <summary>Null when neither placements nor roaming data exist.</summary>
    public IApNeighborOracle? Neighbors { get; init; }

    /// <summary>
    /// Devices excluded on top of the settings' own exclusion sets. Autopilot's release-ripeness
    /// gate holds a device back here rather than by editing the site's stored exclusions.
    /// </summary>
    public IReadOnlyCollection<string> AdditionalExcludedMacs { get; init; } = [];
}

/// <summary>Release channel string constants as the UniFi API spells them.</summary>
public static class FirmwareChannels
{
    public const string Release = "release";
    public const string ReleaseCandidate = "release-candidate";
    public const string Beta = "beta";
}

/// <summary>
/// Spacing knobs resolved from the profile preset plus any advanced JSON overrides.
/// </summary>
public class ResolvedSpacing
{
    public int ApGapSeconds { get; init; }
    public int SwitchGapSeconds { get; init; }
    public int GatewayGapSeconds { get; init; }
    public int MaxApParallelism { get; init; }
    public int MaxSwitchParallelism { get; init; }

    /// <summary>Advanced-override JSON shape; every field optional on top of the preset.</summary>
    private class Overrides
    {
        [JsonPropertyName("apGapSeconds")] public int? ApGapSeconds { get; set; }
        [JsonPropertyName("switchGapSeconds")] public int? SwitchGapSeconds { get; set; }
        [JsonPropertyName("gatewayGapSeconds")] public int? GatewayGapSeconds { get; set; }
        [JsonPropertyName("maxApParallelism")] public int? MaxApParallelism { get; set; }
        [JsonPropertyName("maxSwitchParallelism")] public int? MaxSwitchParallelism { get; set; }
    }

    public static ResolvedSpacing For(FirmwareSpacingProfile profile, string? advancedJson)
    {
        var (apGap, swGap, gwGap, apPar, swPar) = profile switch
        {
            FirmwareSpacingProfile.Conservative => (180, 300, 600, 1, 1),
            FirmwareSpacingProfile.Fast => (60, 90, 120, 6, 4),
            _ => (120, 180, 300, 3, 2),
        };

        Overrides? o = null;
        if (!string.IsNullOrWhiteSpace(advancedJson))
        {
            try { o = JsonSerializer.Deserialize<Overrides>(advancedJson); }
            catch (JsonException) { }
        }

        return new ResolvedSpacing
        {
            ApGapSeconds = Math.Max(0, o?.ApGapSeconds ?? apGap),
            SwitchGapSeconds = Math.Max(0, o?.SwitchGapSeconds ?? swGap),
            GatewayGapSeconds = Math.Max(0, o?.GatewayGapSeconds ?? gwGap),
            MaxApParallelism = Math.Max(1, o?.MaxApParallelism ?? apPar),
            MaxSwitchParallelism = Math.Max(1, o?.MaxSwitchParallelism ?? swPar),
        };
    }
}

/// <summary>Exclusion sets parsed from FirmwareRolloutSettings.ExclusionsJson.</summary>
public class RolloutExclusions
{
    public HashSet<string> Macs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Skus { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DeviceTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    private class Shape
    {
        [JsonPropertyName("macs")] public List<string>? Macs { get; set; }
        [JsonPropertyName("skus")] public List<string>? Skus { get; set; }
        [JsonPropertyName("deviceTypes")] public List<string>? DeviceTypes { get; set; }
    }

    public static RolloutExclusions Parse(string? json)
    {
        var result = new RolloutExclusions();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            var shape = JsonSerializer.Deserialize<Shape>(json);
            foreach (var m in shape?.Macs ?? []) result.Macs.Add(MacNormalizer.Normalize(m));
            foreach (var s in shape?.Skus ?? []) result.Skus.Add(s);
            foreach (var t in shape?.DeviceTypes ?? []) result.DeviceTypes.Add(t);
        }
        catch (JsonException) { }
        return result;
    }

    public bool Excludes(PlannerDevice d) =>
        Macs.Contains(d.Mac) || Skus.Contains(d.Model) ||
        DeviceTypes.Contains(FirmwareDeviceTypes.Code(d.Type)) || DeviceTypes.Contains(d.Type.ToString());
}

/// <summary>Maps the effective device type to the UniFi short code stored on steps.</summary>
public static class FirmwareDeviceTypes
{
    public static string Code(DeviceType type) => type switch
    {
        DeviceType.AccessPoint => "uap",
        DeviceType.Switch => "usw",
        DeviceType.Gateway => "ugw",
        _ => type.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Reads a stored step's type code back. The executor needs the type to pick a device's
    /// offline budget, and the step row is all it has once the plan is persisted.
    /// </summary>
    public static DeviceType Parse(string? code) => code?.ToLowerInvariant() switch
    {
        "uap" => DeviceType.AccessPoint,
        "usw" => DeviceType.Switch,
        "ugw" => DeviceType.Gateway,
        _ => Enum.TryParse<DeviceType>(code, ignoreCase: true, out var parsed) ? parsed : DeviceType.Unknown,
    };
}

/// <summary>Lowercase-with-colons MAC normalization (the planner-wide key space).</summary>
public static class MacNormalizer
{
    public static string Normalize(string mac)
    {
        if (string.IsNullOrEmpty(mac)) return string.Empty;
        var bare = mac.Replace(":", "").Replace("-", "").ToLowerInvariant();
        if (bare.Length != 12) return mac.Trim().ToLowerInvariant().Replace('-', ':');
        return string.Join(':', Enumerable.Range(0, 6).Select(i => bare.Substring(i * 2, 2)));
    }
}

/// <summary>
/// The serializable plan document persisted as FirmwareRolloutPlan.PlanJson - waves,
/// per-step ETAs, channel groups, mesh re-pairs, and the assumptions the UI surfaces.
/// </summary>
public class RolloutPlanDocument
{
    public List<PlanChannelGroup> ChannelGroups { get; set; } = [];
    public List<PlanWave> Waves { get; set; } = [];
    public List<PlanMeshRepair> MeshRepairs { get; set; } = [];

    /// <summary>Whether the UniFi Network application updates first (wave 0 timeline entry).</summary>
    public bool IncludesUniFiNetworkUpdate { get; set; }
    public int UniFiNetworkUpdateSeconds { get; set; }

    /// <summary>Whether the gateway step includes the UniFi OS cycle on a Cloud Gateway.</summary>
    public bool IncludesUniFiOsUpdate { get; set; }

    public int TotalEstimatedSeconds { get; set; }

    /// <summary>Human-readable assumptions and fallbacks used (shown in the preview).</summary>
    public List<string> Notes { get; set; } = [];

    /// <summary>
    /// Highest wave a Site Admin has approved, when the plan runs with per-wave approval. Zero
    /// means nothing is approved yet; the setting is off for most plans and this stays unused.
    /// </summary>
    public int ApprovedThroughWave { get; set; }

    /// <summary>
    /// The wave a paused plan is waiting on. Set alongside the Paused status so a resume knows
    /// which wave it is releasing, and cleared when it is released.
    /// </summary>
    public int? WaitingApprovalWave { get; set; }

    /// <summary>
    /// Image URLs for the versions devices were on BEFORE the rollout, resolved at plan time. The
    /// console catalog carries latest-only, so a rollback has nowhere else to read these from once
    /// the upgrade has happened. Entries with no URL record that the version was unresolvable.
    /// </summary>
    public List<PlanPriorVersion> PriorVersions { get; set; } = [];

    /// <summary>Progress of the UniFi Network application update that runs ahead of wave 1.</summary>
    public RolloutConsoleStepState NetworkAppUpdate { get; set; } = new();

    /// <summary>Progress of the UniFi OS update that runs after every device step.</summary>
    public RolloutConsoleStepState UniFiOsUpdate { get; set; } = new();

    /// <summary>Console channels this rollout has already set.</summary>
    public RolloutConsoleChannels ConsoleChannels { get; set; } = new();

    /// <summary>Time this rollout could not see the site, which no deadline counts.</summary>
    public RolloutVisibility Visibility { get; set; } = new();
}

/// <summary>
/// The rollout's own sight of the site, persisted because it outlives the process: a server that
/// was down saw nothing, and that gap has to be charged as blind time on the pass after it comes
/// back rather than to whatever device happened to be mid-cycle.
/// </summary>
public class RolloutVisibility
{
    /// <summary>End of the last pass. The gap from here to the next pass is time nothing was watched.</summary>
    public DateTime? LastTickAt { get; set; }

    /// <summary>Start of the blind spell in progress, or null while the site is visible.</summary>
    public DateTime? BlindSince { get; set; }

    /// <summary>Blind spells that have ended.</summary>
    public List<RolloutBlindInterval> Blind { get; set; } = [];

    /// <summary>Whether this blind spell has already been announced, so it is announced once.</summary>
    public bool LostAnnounced { get; set; }
}

/// <summary>One stretch of time the rollout had no sight of the site.</summary>
public class RolloutBlindInterval
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
}

/// <summary>
/// The console-level channels a rollout put in force, recorded so a resume neither sets one twice
/// nor loses track of what it changed. A null field means that surface was left alone.
/// </summary>
public class RolloutConsoleChannels
{
    /// <summary>Channel the UniFi Network application was moved to, when this rollout moved it.</summary>
    public string? NetworkAppChannel { get; set; }

    /// <summary>Channel UniFi OS was moved to, when this rollout moved it.</summary>
    public string? UniFiOsChannel { get; set; }
}

/// <summary>
/// One console-level update's progress. Persisted inside the plan document because a console
/// update outlives any in-memory state: it takes the API down with it, and a server restart
/// during one must never fire the trigger a second time.
/// </summary>
public class RolloutConsoleStepState
{
    /// <summary>Whether the install has been commanded. The resume guard.</summary>
    public bool Triggered { get; set; }

    /// <summary>When it was commanded, which the recovery budget runs from.</summary>
    public DateTime? TriggeredAt { get; set; }

    /// <summary>Whether there is anything left to wait for.</summary>
    public bool Settled { get; set; }

    /// <summary>
    /// How it ended: "updated", "nothing-to-update", "refused", "unchanged", "stuck", or "skipped".
    /// </summary>
    public string? Outcome { get; set; }

    /// <summary>Version the install was aiming at, where the console named one.</summary>
    public string? TargetVersion { get; set; }
}

/// <summary>
/// One device's pre-rollout image, cached so a rollback can be run without the release feed
/// having to answer at the moment somebody needs it.
/// </summary>
public class PlanPriorVersion
{
    /// <summary>Normalized device MAC.</summary>
    public string Mac { get; set; } = string.Empty;

    /// <summary>Version the device was on before the rollout.</summary>
    public string? Version { get; set; }

    /// <summary>Direct image URL, or null when the feed carries no such build (RC and EA are not public).</summary>
    public string? Url { get; set; }

    /// <summary>Why the URL is missing, when it is.</summary>
    public string? UnavailableReason { get; set; }
}

public class PlanWave
{
    public int Number { get; set; }
    public string Channel { get; set; } = string.Empty;
    public int StartOffsetSeconds { get; set; }
    public List<PlanWaveStep> Steps { get; set; } = [];
}

public class PlanWaveStep
{
    public string Mac { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string DisplayModel { get; set; } = string.Empty;
    public string DeviceType { get; set; } = string.Empty;
    public string? FromVersion { get; set; }
    public string? ToVersion { get; set; }
    public bool IsCanary { get; set; }
    public bool HeldForCanary { get; set; }
    public bool IsMeshParticipant { get; set; }
    public int EstimatedDowntimeSeconds { get; set; }
    public int OfflineBudgetSeconds { get; set; }
    public int EtaOffsetSeconds { get; set; }
}

public class PlanChannelGroup
{
    public string Channel { get; set; } = string.Empty;
    public bool RequiresConsoleChange { get; set; }
    public int FirstWave { get; set; }
    public int LastWave { get; set; }
    public int DeviceCount { get; set; }
}

public class PlanMeshRepair
{
    public string ChildMac { get; set; } = string.Empty;
    public string ChildName { get; set; } = string.Empty;
    public string? ChildIp { get; set; }
    public string? ParentMac { get; set; }
    public string? Iface { get; set; }

    /// <summary>Re-pair is enqueued once every wave up to and including this one completed.</summary>
    public int AfterWave { get; set; }
}

/// <summary>Planner output: the document plus persistable step rows.</summary>
public class RolloutPlanResult
{
    public required RolloutPlanDocument Document { get; init; }
    public required List<FirmwareRolloutStep> Steps { get; init; }
}

/// <summary>Coarse site classification for the no-history quiet-window fallback.</summary>
public enum SiteUsageProfile
{
    Home,
    Business
}

/// <summary>A proposed rollout start window.</summary>
public class QuietWindowProposal
{
    public DayOfWeek Day { get; init; }
    public int Hour { get; init; }

    /// <summary>Next occurrence of the window in site-local time.</summary>
    public DateTime StartLocal { get; init; }

    /// <summary>Mean busy fraction over the rollout duration (0 = idle history, 1 = saturated).</summary>
    public double BusyScore { get; init; }

    /// <summary>True when no usable history existed and the heuristic fallback picked the window.</summary>
    public bool UsedFallback { get; init; }

    /// <summary>What the proposal was based on, for the wizard ("7-day usage history", "home-profile default", ...).</summary>
    public string Basis { get; init; } = string.Empty;
}
