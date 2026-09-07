using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Per-WAN congestion profile learned by Adaptive SQM, plus the state of the learning run that
/// produced it. One row per WAN number. Kept apart from <see cref="SqmWanConfiguration"/> on
/// purpose: that row is rewritten from the page model on every deploy, and learning state stored
/// there would be reset with it.
/// </summary>
public class SqmCongestionProfile
{
    [Key]
    public int Id { get; set; }

    /// <summary>WAN identifier (1 or 2), matching <see cref="SqmWanConfiguration.WanNumber"/>.</summary>
    public int WanNumber { get; set; }

    /// <summary>Data-path WAN interface the samples were taken on (e.g. "eth4", "ppp0").</summary>
    [MaxLength(50)]
    public string Interface { get; set; } = "";

    /// <summary>Friendly WAN name at the time learning started.</summary>
    [MaxLength(100)]
    public string Name { get; set; } = "";

    /// <summary>Connection type (NetworkOptimizer.Sqm.Models.ConnectionType) at the time learning started.</summary>
    public int ConnectionType { get; set; }

    /// <summary>The scheduled task that drives the hourly samples; null once deleted.</summary>
    public int? ScheduledTaskId { get; set; }

    /// <summary>When the current learning run started (UTC).</summary>
    public DateTime? LearningStartedAt { get; set; }

    /// <summary>When the current learning run is due to end (UTC).</summary>
    public DateTime? LearningEndsAt { get; set; }

    /// <summary>When the run completed or was stopped (UTC); null while learning.</summary>
    public DateTime? LearningCompletedAt { get; set; }

    /// <summary>Seconds per direction handed to the speed test binary for each sample.</summary>
    public int SampleDurationSeconds { get; set; } = 4;

    /// <summary>When the last successful sample was taken (UTC).</summary>
    public DateTime? LastSampleAt { get; set; }

    /// <summary>Most recent sample failure, cleared by the next success.</summary>
    [MaxLength(500)]
    public string? LastError { get; set; }

    /// <summary>Failed attempts since the last success; drives how often failures alert.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>168 download multipliers as a JSON array, slot = day * 24 + hour, day 0 = Monday.</summary>
    public string? DownloadMultipliersJson { get; set; }

    /// <summary>168 upload multipliers as a JSON array.</summary>
    public string? UploadMultipliersJson { get; set; }

    /// <summary>168 per-slot valid sample counts as a JSON array.</summary>
    public string? SampleCountsJson { get; set; }

    /// <summary>Best-hour download throughput the multipliers are relative to.</summary>
    public double PeakDownloadMbps { get; set; }

    /// <summary>Best-hour upload throughput the multipliers are relative to.</summary>
    public double PeakUploadMbps { get; set; }

    /// <summary>Samples that survived validation and outlier rejection.</summary>
    public int ValidSampleCount { get; set; }

    /// <summary>Percentage of the 168 hour-of-week slots holding a sample of their own.</summary>
    public double CoveragePercent { get; set; }

    /// <summary>Distinct calendar days with a valid sample.</summary>
    public int DaysSpanned { get; set; }

    /// <summary>True once the learner judges the profile fit to shape from.</summary>
    public bool IsReliable { get; set; }

    /// <summary>When the multipliers were last recomputed (UTC).</summary>
    public DateTime? ProfileUpdatedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>True when a learned curve exists, reliable or not.</summary>
    public bool HasProfile => !string.IsNullOrEmpty(DownloadMultipliersJson) && ValidSampleCount > 0;
}
