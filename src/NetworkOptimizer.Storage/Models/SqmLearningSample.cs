using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// One Adaptive SQM learning sample: a brief gateway speed test on a WAN taken while the link was
/// idle. Raw material for <see cref="SqmCongestionProfile"/>; kept so the profile can be
/// recomputed and so the page can show what was measured and what was thrown out.
/// </summary>
public class SqmLearningSample
{
    [Key]
    public int Id { get; set; }

    /// <summary>WAN identifier (1 or 2).</summary>
    public int WanNumber { get; set; }

    /// <summary>When the sample was taken (UTC).</summary>
    public DateTime SampledAt { get; set; } = DateTime.UtcNow;

    /// <summary>Gateway-local day of week at sample time, 0 = Monday, the slot the schedule reads.</summary>
    public int LocalDayOfWeek { get; set; }

    /// <summary>Gateway-local hour at sample time (0-23).</summary>
    public int LocalHour { get; set; }

    public double DownloadMbps { get; set; }

    public double UploadMbps { get; set; }

    /// <summary>Unloaded latency reported by the test, when available.</summary>
    public double? LatencyMs { get; set; }

    public double? DownloadLoadedLatencyMs { get; set; }

    public double? UploadLoadedLatencyMs { get; set; }

    /// <summary>WAN download traffic observed just before the test (the idle check).</summary>
    public double IdleDownloadMbps { get; set; }

    /// <summary>WAN upload traffic observed just before the test (the idle check).</summary>
    public double IdleUploadMbps { get; set; }

    /// <summary>Where the idle reading came from: "snmp" (monitored counter interface) or "gateway" (data-path counters over SSH).</summary>
    [MaxLength(20)]
    public string IdleSource { get; set; } = "gateway";

    /// <summary>Rate the shaper was lifted to on the download side for this sample; null when it was not lifted.</summary>
    public int? LiftDownloadMbps { get; set; }

    /// <summary>Rate the shaper was lifted to on the upload side for this sample; null when it was not lifted.</summary>
    public int? LiftUploadMbps { get; set; }

    /// <summary>True when either direction landed within a few percent of its lift: the sample measured the lift, not the line.</summary>
    public bool ProbeLimited { get; set; }

    /// <summary>False when the test itself failed; such rows carry <see cref="Error"/> and no throughput.</summary>
    public bool Success { get; set; }

    [MaxLength(500)]
    public string? Error { get; set; }

    /// <summary>Set by the learner when the sample was left out of the profile.</summary>
    public bool Excluded { get; set; }

    [MaxLength(200)]
    public string? ExclusionReason { get; set; }
}
