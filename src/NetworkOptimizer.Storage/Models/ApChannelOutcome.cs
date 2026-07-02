using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Long-term measured outcome for one AP radio config: a daily aggregate of channel utilization,
/// interference, and TX retry percentages attributed to the (channel, width) that was actually
/// live when each sample was taken. This is the Channel Recommendation engine's persistent
/// memory of how tried configs really performed - it outlives the UniFi Console's short
/// metrics retention, so channels the AP sat on weeks or months ago keep their measured
/// ground truth instead of falling back to inferred scores.
/// </summary>
public class ApChannelOutcome
{
    [Key]
    public int Id { get; set; }

    /// <summary>AP MAC address (lowercase, colon-separated)</summary>
    [Required]
    [MaxLength(17)]
    public string ApMac { get; set; } = "";

    /// <summary>Radio band code - "ng" (2.4 GHz), "na" (5 GHz), "6e" (6 GHz)</summary>
    [Required]
    [MaxLength(10)]
    public string Band { get; set; } = "";

    /// <summary>Control channel the samples are attributed to</summary>
    public int Channel { get; set; }

    /// <summary>Channel width in MHz at sample time; 0 when unknown (samples attributed to a
    /// channel the radio had already left, where the width at the time can't be recovered)</summary>
    public int WidthMhz { get; set; }

    /// <summary>UTC date bucket (midnight) the samples fall into</summary>
    public DateTime BucketDate { get; set; }

    /// <summary>Sum of channel utilization percentages across samples (divide by SampleCount for avg)</summary>
    public double UtilizationSum { get; set; }

    /// <summary>Sum of interference percentages across samples</summary>
    public double InterferenceSum { get; set; }

    /// <summary>Sum of TX retry percentages across samples</summary>
    public double TxRetrySum { get; set; }

    /// <summary>Number of samples aggregated into this bucket</summary>
    public int SampleCount { get; set; }

    /// <summary>Timestamp of the most recent sample in this bucket (UTC)</summary>
    public DateTime LastSampleUtc { get; set; }
}
