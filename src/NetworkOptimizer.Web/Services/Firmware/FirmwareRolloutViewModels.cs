using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// Channel options the console offers, and the one devices follow today. The options list IS the
/// early-access check: EA is only offerable where the console lists it.
/// </summary>
public class RolloutChannelAvailability
{
    /// <summary>Channel UniFi devices follow right now.</summary>
    public string CurrentDeviceChannel { get; set; } = FirmwareChannels.Release;

    /// <summary>Device channel options this console offers.</summary>
    public List<string> AvailableDeviceChannels { get; set; } = [];

    /// <summary>UniFi Network application channel options this console offers.</summary>
    public List<string> AvailableNetworkAppChannels { get; set; } = [];

    /// <summary>Whether early access (beta) may be offered for devices on this console.</summary>
    public bool EarlyAccessAvailable => AvailableDeviceChannels
        .Any(c => string.Equals(c, FirmwareChannels.Beta, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One device's slot in a plan, as the preview, the live view and the report render it.</summary>
public class RolloutStepView
{
    public int Id { get; set; }
    public string Mac { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string DeviceType { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public string? FromVersion { get; set; }
    public string? ToVersion { get; set; }
    public int Wave { get; set; }
    public FirmwareRolloutStepState State { get; set; }
    public DateTime? CommandedAt { get; set; }
    public DateTime? WentDownAt { get; set; }
    public DateTime? BackAt { get; set; }
    public int? DowntimeSeconds { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// Whether a one-click rollback can be offered: the device is upgraded and an image for the
    /// version it came from was cached at plan time.
    /// </summary>
    public bool CanRollBack { get; set; }

    /// <summary>Why no rollback is offered, when none is.</summary>
    public string? RollbackUnavailableReason { get; set; }
}

/// <summary>A plan with its parsed document and current per-device state.</summary>
public class RolloutPlanView
{
    public int Id { get; set; }
    public FirmwareRolloutStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? ScheduledStartAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>The waves, timeline, channel groups and notes the plan was built with.</summary>
    public RolloutPlanDocument Plan { get; set; } = new();

    /// <summary>Every step, in execution order.</summary>
    public List<RolloutStepView> Steps { get; set; } = [];

    /// <summary>Whether the post-soak report has been built.</summary>
    public bool HasReport { get; set; }
}

/// <summary>One row of the rollout history list.</summary>
public class RolloutPlanSummaryView
{
    public int Id { get; set; }
    public FirmwareRolloutStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? ScheduledStartAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>Devices the plan set out to upgrade.</summary>
    public int DeviceCount { get; set; }

    /// <summary>Waves the plan was split into.</summary>
    public int WaveCount { get; set; }

    public bool HasReport { get; set; }
}

/// <summary>
/// A finished rollout's report. The per-device rows are the step history; the aggregate blob is
/// whatever the soak report generator wrote, which Phase 7 gives a typed shape.
/// </summary>
public class RolloutReportView
{
    public int PlanId { get; set; }
    public FirmwareRolloutStatus Status { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>False while the rollout is still soaking, so the page can say so rather than 404.</summary>
    public bool IsReady { get; set; }

    /// <summary>The persisted soak report, or null while it is still being soaked for.</summary>
    public string? ReportJson { get; set; }

    /// <summary>Per-device outcomes: versions, downtime, litmus state and any error.</summary>
    public List<RolloutStepView> Steps { get; set; } = [];
}

/// <summary>
/// The wizard's dry run: what would be upgraded, in what order, when it should start, and
/// everything the site's console says that changes the answer.
/// </summary>
public class RolloutPreviewView
{
    /// <summary>The plan that would be created, unpersisted.</summary>
    public RolloutPlanDocument Plan { get; set; } = new();

    /// <summary>Every step the plan would create, including the excluded ones (dimmed in the UI).</summary>
    public List<RolloutStepView> Steps { get; set; } = [];

    /// <summary>Proposed start window, from the site's own usage history where it has any.</summary>
    public QuietWindowProposal? ProposedWindow { get; set; }

    /// <summary>Channel options, including whether early access can be offered.</summary>
    public RolloutChannelAvailability Channels { get; set; } = new();

    /// <summary>Devices the console reports, upgradable or not.</summary>
    public int TotalDeviceCount { get; set; }

    /// <summary>Devices that would be upgraded.</summary>
    public int UpgradableCount { get; set; }

    /// <summary>Upgradable devices held back by an exclusion.</summary>
    public int ExcludedCount { get; set; }

    /// <summary>Whether the console answered at all. Nothing can be planned against a dark console.</summary>
    public bool ConsoleConnected { get; set; }

    /// <summary>
    /// True on a self-hosted UniFi OS Server. Its OS is never ours to update, so the wizard hides
    /// the UniFi OS option; the UniFi Network application stays in scope.
    /// </summary>
    public bool IsStandaloneConsole { get; set; }

    /// <summary>Whether UniFi's own nightly device auto-upgrade is on. It races a rollout.</summary>
    public bool ConsoleAutoUpgradeEnabled { get; set; }

    /// <summary>Whether the console updates its own UniFi OS on a schedule.</summary>
    public bool ConsoleOsAutoUpdateEnabled { get; set; }

    /// <summary>Whether that schedule also updates the applications (UniFi Network among them).</summary>
    public bool ConsoleAppsAutoUpdateEnabled { get; set; }

    /// <summary>True when a rollout is already scheduled or running, so a new one cannot be started.</summary>
    public bool HasActivePlan { get; set; }

    /// <summary>Things the wizard should say out loud before the Start button is pressed.</summary>
    public List<string> Warnings { get; set; } = [];
}
