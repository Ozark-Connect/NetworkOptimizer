using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Learned upgrade downtime per model. Estimates prefer this site's own measurements
/// over the seed table once a model has been seen here.
/// </summary>
public class FirmwareModelTiming
{
    [Key]
    public int Id { get; set; }

    /// <summary>Model / SKU this timing belongs to. One row per model.</summary>
    [Required]
    [MaxLength(50)]
    public string Model { get; set; } = string.Empty;

    /// <summary>Lifetime number of upgrades measured for this model, including samples aged out of the window.</summary>
    public int SampleCount { get; set; }

    /// <summary>Median offline window across the retained samples.</summary>
    public int MedianDowntimeSeconds { get; set; }

    /// <summary>90th-percentile offline window across the retained samples.</summary>
    public int P90DowntimeSeconds { get; set; }

    /// <summary>
    /// The most recent raw downtime samples, oldest first, as a JSON array of seconds.
    /// Percentiles cannot be recomputed from the aggregates alone, so the window they are
    /// derived from is kept here rather than in a separate table.
    /// </summary>
    [Required]
    public string RecentSamplesJson { get; set; } = "[]";

    /// <summary>When the last sample landed.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
