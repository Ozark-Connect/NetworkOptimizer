using System.ComponentModel.DataAnnotations;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Storage.Models;

/// <summary>When a requested device restart should run.</summary>
public enum DeviceRestartMode
{
    /// <summary>As soon as the executor reaches it.</summary>
    Now = 0,

    /// <summary>At a time the user chose.</summary>
    At = 1,

    /// <summary>At the next quiet hour for the device and everything below it.</summary>
    Quiet = 2,
}

/// <summary>How a restart is carried out.</summary>
public enum DeviceRestartMethod
{
    /// <summary>SSH when it works, then the UniFi Console, then a PoE power-cycle as the last attempt.</summary>
    Auto = 0,

    /// <summary>A <c>reboot</c> over SSH.</summary>
    Ssh = 1,

    /// <summary>The console's <c>cmd/devmgr restart</c>.</summary>
    Console = 2,

    /// <summary>Power-cycle the PoE port on the switch that powers the device.</summary>
    PoeCycle = 3,
}

/// <summary>Where a restart request is.</summary>
public enum DeviceRestartStatus
{
    /// <summary>Waiting for its time.</summary>
    Pending = 0,

    /// <summary>Due, but held by a lockout (firmware upgrade, provisioning, a rollout).</summary>
    Waiting = 1,

    /// <summary>In the executor's current batch.</summary>
    Running = 2,

    /// <summary>The device restarted and came back.</summary>
    Done = 3,

    /// <summary>The device booted during its batch because something above it restarted.</summary>
    DoneByUpstream = 4,

    /// <summary>The command failed on every attempt, or the device never confirmed a restart.</summary>
    Failed = 5,

    /// <summary>Cancelled by a user, or replaced by a newer request for the same device.</summary>
    Cancelled = 6,
}

/// <summary>
/// One requested restart of one UniFi device. The executor runs open requests in batches, root
/// first down the uplink tree, and settles each one to a terminal status.
/// </summary>
public class DeviceRestartRequest
{
    [Key]
    public int Id { get; set; }

    /// <summary>Normalized device MAC (lowercase, colon-separated).</summary>
    [Required, MaxLength(50)]
    public string DeviceMac { get; set; } = string.Empty;

    /// <summary>The device's name when requested, for the audit, alerts, and the reboot reason.</summary>
    [MaxLength(200)]
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>The device's type when requested.</summary>
    public DeviceType DeviceType { get; set; }

    public DeviceRestartMode Mode { get; set; }

    /// <summary>When the restart may run: the request time, the chosen time, or the quiet hour.</summary>
    public DateTime DueAtUtc { get; set; }

    /// <summary>What chose a quiet hour (usage history or a site-profile default); null for other modes.</summary>
    [MaxLength(200)]
    public string? QuietBasis { get; set; }

    /// <summary><see cref="DeviceRestartMethod.Auto"/>, or <see cref="DeviceRestartMethod.PoeCycle"/> when the user chose it.</summary>
    public DeviceRestartMethod RequestedMethod { get; set; }

    public DeviceRestartStatus Status { get; set; }

    /// <summary>Why the request is waiting, or why it failed. Null otherwise.</summary>
    [MaxLength(500)]
    public string? StatusReason { get; set; }

    /// <summary>Commands sent so far.</summary>
    public int Attempts { get; set; }

    /// <summary>When the last command went out; null before the first. The executor confirms the restart against it.</summary>
    public DateTime? LastCommandAt { get; set; }

    /// <summary>The method that actually ran; null until it runs.</summary>
    public DeviceRestartMethod? Method { get; set; }

    /// <summary>The switch whose port was power-cycled, when <see cref="Method"/> is PoE.</summary>
    [MaxLength(50)]
    public string? PoeSwitchMac { get; set; }

    /// <summary>The power-cycled port's <c>port_idx</c>, when <see cref="Method"/> is PoE.</summary>
    public int? PoePortIdx { get; set; }

    /// <summary>The batch this request ran in: the instant that batch started.</summary>
    public DateTime? BatchStartedAt { get; set; }

    [MaxLength(100)]
    public string? RequestedByUserId { get; set; }

    [MaxLength(200)]
    public string? RequestedByName { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>True while the request still has to run.</summary>
    public bool IsOpen => Status is DeviceRestartStatus.Pending or DeviceRestartStatus.Waiting or DeviceRestartStatus.Running;
}
