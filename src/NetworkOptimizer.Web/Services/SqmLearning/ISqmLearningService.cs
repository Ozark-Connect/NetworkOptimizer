using NetworkOptimizer.Sqm.Models;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services.SqmLearning;

/// <summary>
/// Adaptive SQM congestion profile learning for the site in context: starting and stopping the
/// one-time learning schedule, reading its progress, and the learned profile. Starting, stopping,
/// and running a sample operate the deployed system within its envelope, so they are Operator;
/// reads are Viewer.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface ISqmLearningService
{
    /// <summary>Learning state and profile for a WAN; null when nothing has ever been learned or started.</summary>
    [RequireRole(Roles.Viewer)]
    Task<SqmLearningStatus?> GetStatusAsync(int wanNumber);

    /// <summary>Raw samples of the current run, newest first.</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<SqmLearningSample>> GetSamplesAsync(int wanNumber, int count = 200);

    /// <summary>Starts a 7-day learning run for the WAN, creating its hourly schedule. Idempotent while one is active.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.SqmLearningStarted, TargetType = "wan")]
    Task<SqmLearningStatus> StartAsync(SqmLearningStartRequest request);

    /// <summary>Stops the run early, keeping whatever was learned.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.SqmLearningStopped, TargetType = "wan")]
    Task StopAsync(int wanNumber);

    /// <summary>Removes the schedule, the samples, and the learned profile for the WAN.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.SqmLearningCleared, TargetType = "wan")]
    Task ClearAsync(int wanNumber);

    /// <summary>Runs the learning schedule now. False when it is already running or there is none.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.ScheduleChanged, TargetType = "schedule")]
    Task<bool> RunSampleNowAsync(int wanNumber);

    /// <summary>The learned profile as shareable JSON; null when there is none.</summary>
    [RequireRole(Roles.Viewer)]
    Task<string?> ExportProfileJsonAsync(int wanNumber);
}

/// <summary>What the page needs to start learning on a WAN.</summary>
public sealed class SqmLearningStartRequest
{
    public int WanNumber { get; set; }
    public string Interface { get; set; } = "";
    public string Name { get; set; } = "";
    public ConnectionType ConnectionType { get; set; }

    /// <summary>UniFi WAN network group ("WAN", "WAN2"), when the page knows it.</summary>
    public string? WanGroup { get; set; }

    /// <summary>Nominal speeds and shaping facts from the form, used to size the shaper lift.</summary>
    public int NominalDownloadMbps { get; set; }
    public int NominalUploadMbps { get; set; }
    public int? LinkSpeedOverrideMbps { get; set; }
    public bool RateProportionalDownloadBurst { get; set; }

    public int Days { get; set; } = SqmLearningTaskConfig.DefaultDays;
    public int DurationSeconds { get; set; } = SqmLearningTaskConfig.DefaultDurationSeconds;
}

/// <summary>Learning state of one WAN, combining the profile row with its schedule task.</summary>
public sealed class SqmLearningStatus
{
    public int WanNumber { get; set; }
    public string Interface { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>A learned curve exists (reliable or not).</summary>
    public bool HasProfile { get; set; }

    /// <summary>The schedule exists, is enabled, and the window has not ended.</summary>
    public bool IsActive { get; set; }

    /// <summary>The run finished or was stopped.</summary>
    public bool IsCompleted { get; set; }

    public int? TaskId { get; set; }
    public bool TaskEnabled { get; set; }

    /// <summary>A sample is being taken right now.</summary>
    public bool IsRunning { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? LastRunAt { get; set; }
    public DateTime? NextRunAt { get; set; }
    public DateTime? LastSampleAt { get; set; }
    public string? LastStatus { get; set; }
    public string? LastSummary { get; set; }
    public string? LastError { get; set; }

    public int ValidSampleCount { get; set; }
    public int TotalSampleCount { get; set; }
    public double CoveragePercent { get; set; }
    public int DaysSpanned { get; set; }
    public bool IsReliable { get; set; }
    public double PeakDownloadMbps { get; set; }
    public double PeakUploadMbps { get; set; }
    public int DurationSeconds { get; set; }

    /// <summary>The learned curves, when <see cref="HasProfile"/>.</summary>
    public LearnedCongestionProfile? Profile { get; set; }
}
