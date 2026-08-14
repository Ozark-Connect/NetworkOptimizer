using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// How much of the firmware rollout machinery the site has turned on.
/// </summary>
public enum FirmwareRolloutMode
{
    /// <summary>No rollouts, manual or automatic.</summary>
    Off = 0,
    /// <summary>Rollouts only run when an admin starts or schedules one.</summary>
    ManualOnly = 1,
    /// <summary>The site schedules its own rollouts into quiet windows.</summary>
    Autopilot = 2
}

/// <summary>
/// Preset controlling the gap left between devices (and how much runs in parallel).
/// </summary>
public enum FirmwareSpacingProfile
{
    Conservative = 0,
    Balanced = 1,
    Fast = 2
}

/// <summary>
/// Whether autopilot picks its own start window from the usage fingerprint or uses
/// a day/hour the user pinned.
/// </summary>
public enum FirmwareAutopilotWindowMode
{
    /// <summary>Quietest suitable window, chosen from measured usage.</summary>
    Auto = 0,
    /// <summary>The day-of-week and hour in FixedDayOfWeek / FixedHour.</summary>
    Fixed = 1
}

/// <summary>
/// Site-wide Firmware Rollout configuration. Single row per site database.
/// </summary>
public class FirmwareRolloutSettings
{
    [Key]
    public int Id { get; set; }

    /// <summary>Off, manual-only, or autopilot.</summary>
    public FirmwareRolloutMode Mode { get; set; } = FirmwareRolloutMode.ManualOnly;

    /// <summary>
    /// Release channel applied to every device that has no per-device-type or per-SKU
    /// override (e.g. "release", "release-candidate", "beta").
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string GlobalChannel { get; set; } = "release";

    /// <summary>JSON map of UniFi device type to channel, overriding GlobalChannel.</summary>
    [Required]
    public string PerDeviceTypeChannelsJson { get; set; } = "{}";

    /// <summary>JSON map of SKU/model to channel, overriding both GlobalChannel and the per-type map.</summary>
    [Required]
    public string PerSkuChannelsJson { get; set; } = "{}";

    /// <summary>Include the UniFi OS update on Cloud Gateways.</summary>
    public bool IncludeUniFiOs { get; set; } = true;

    /// <summary>Include the UniFi Network application update (runs first when included).</summary>
    public bool IncludeUniFiNetwork { get; set; } = true;

    /// <summary>JSON exclusion sets: device MACs, SKUs, and device types that never get upgraded.</summary>
    [Required]
    public string ExclusionsJson { get; set; } = "{}";

    /// <summary>Spacing preset; AdvancedSpacingJson refines it when set.</summary>
    public FirmwareSpacingProfile SpacingProfile { get; set; } = FirmwareSpacingProfile.Balanced;

    /// <summary>
    /// JSON overrides on top of SpacingProfile: per-class spacing seconds and the maximum
    /// number of APs upgraded in parallel. Null means the preset alone decides.
    /// </summary>
    public string? AdvancedSpacingJson { get; set; }

    /// <summary>Mute device.offline / device.rebooted alerts for devices inside their rollout window.</summary>
    public bool SuppressStandardAlerts { get; set; } = true;

    /// <summary>Whether autopilot picks the window itself or uses FixedDayOfWeek / FixedHour.</summary>
    public FirmwareAutopilotWindowMode AutopilotWindowMode { get; set; } = FirmwareAutopilotWindowMode.Auto;

    /// <summary>Pinned day of week (0 = Sunday) for Fixed window mode; null in Auto mode.</summary>
    public int? FixedDayOfWeek { get; set; }

    /// <summary>Pinned local hour (0-23) for Fixed window mode; null in Auto mode.</summary>
    public int? FixedHour { get; set; }

    /// <summary>How far ahead of an autopilot run the heads-up alert is published.</summary>
    public int NotifyHoursAhead { get; set; } = 12;

    /// <summary>How long after completion the report waits before it is built.</summary>
    public int SoakHours { get; set; } = 24;

    /// <summary>Minimum age of a published firmware release before autopilot will roll it out. 0 disables the gate.</summary>
    public int MinReleaseAgeDays { get; set; }

    /// <summary>Skip the pre-flight console backup that otherwise blocks the first upgrade command.</summary>
    public bool WaiveBackup { get; set; }

    /// <summary>Pause at every wave boundary until a Site Admin approves the next wave.</summary>
    public bool PerWaveApproval { get; set; }

    /// <summary>When these settings were last written.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
