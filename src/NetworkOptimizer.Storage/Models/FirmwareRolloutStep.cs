using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Per-device state machine. Persisted after every transition so a server restart
/// resumes a rollout instead of restarting it.
/// </summary>
public enum FirmwareRolloutStepState
{
    /// <summary>Queued, nothing sent yet.</summary>
    Pending = 0,
    /// <summary>Waiting on the canary of the same SKU to pass its litmus.</summary>
    Held = 1,
    /// <summary>Upgrade command accepted by the device.</summary>
    Commanded = 2,
    /// <summary>Device has gone offline for the upgrade reboot.</summary>
    Down = 3,
    /// <summary>Device answered again.</summary>
    BackOnline = 4,
    /// <summary>Settling before litmus so boot spikes are not read as regressions.</summary>
    CoolDown = 5,
    /// <summary>Post-upgrade checks passed.</summary>
    LitmusPassed = 6,
    /// <summary>Back and upgraded, but resource use moved enough to warrant a look.</summary>
    RegressionFlagged = 7,
    /// <summary>Upgrade failed, or the device never came back inside its class threshold.</summary>
    Failed = 8,
    /// <summary>Never attempted: excluded by MAC, SKU, or device type.</summary>
    SkippedExcluded = 9,
    /// <summary>Never attempted: this SKU's canary failed, so the rest of the SKU was dropped.</summary>
    AbortedSku = 10
}

/// <summary>
/// One device's slot in a rollout, carrying the versions involved and the measured
/// downtime and before/after resource samples.
/// </summary>
public class FirmwareRolloutStep
{
    [Key]
    public int Id { get; set; }

    /// <summary>Owning plan.</summary>
    public int PlanId { get; set; }

    /// <summary>Colonized device MAC (reboot events use a colon-less form and must be normalized before comparison).</summary>
    [Required]
    [MaxLength(20)]
    public string DeviceMac { get; set; } = string.Empty;

    /// <summary>Device name as the console shows it, captured at plan time.</summary>
    [Required]
    [MaxLength(200)]
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>Model / SKU, the key the canary and the timing store group on.</summary>
    [Required]
    [MaxLength(50)]
    public string Model { get; set; } = string.Empty;

    /// <summary>UniFi device type (uap, usw, ugw, ...), which picks the offline threshold class.</summary>
    [Required]
    [MaxLength(50)]
    public string DeviceType { get; set; } = string.Empty;

    /// <summary>Firmware version running before the upgrade.</summary>
    [MaxLength(100)]
    public string? FromVersion { get; set; }

    /// <summary>Firmware version this step targets.</summary>
    [MaxLength(100)]
    public string? ToVersion { get; set; }

    /// <summary>Effective release channel for this device.</summary>
    [Required]
    [MaxLength(50)]
    public string Channel { get; set; } = string.Empty;

    /// <summary>Wave number; devices in one wave may run in parallel.</summary>
    public int Wave { get; set; }

    /// <summary>Current state.</summary>
    public FirmwareRolloutStepState State { get; set; } = FirmwareRolloutStepState.Pending;

    /// <summary>When the upgrade command was issued.</summary>
    public DateTime? CommandedAt { get; set; }

    /// <summary>When the device was first seen offline.</summary>
    public DateTime? WentDownAt { get; set; }

    /// <summary>When the device answered again.</summary>
    public DateTime? BackAt { get; set; }

    /// <summary>Measured offline window, the sample fed back into FirmwareModelTiming.</summary>
    public int? DowntimeSeconds { get; set; }

    /// <summary>Pre-upgrade hour-mean CPU/memory and loss, serialized.</summary>
    public string? PreStatsJson { get; set; }

    /// <summary>Post-cool-down hour-mean CPU/memory and loss, serialized.</summary>
    public string? PostStatsJson { get; set; }

    /// <summary>Failure detail when State is Failed.</summary>
    [MaxLength(1000)]
    public string? Error { get; set; }
}
