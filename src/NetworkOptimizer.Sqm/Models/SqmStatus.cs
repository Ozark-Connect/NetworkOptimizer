namespace NetworkOptimizer.Sqm.Models;

/// <summary>
/// Current status of SQM system
/// </summary>
public class SqmStatus
{
    /// <summary>
    /// Current download rate in Mbps
    /// </summary>
    public double CurrentRate { get; set; }

    /// <summary>
    /// Last speedtest result in Mbps
    /// </summary>
    public double? LastSpeedtest { get; set; }

    /// <summary>
    /// Timestamp of last speedtest
    /// </summary>
    public DateTime? LastSpeedtestTime { get; set; }

    /// <summary>
    /// Current latency in milliseconds
    /// </summary>
    public double? CurrentLatency { get; set; }

    /// <summary>
    /// Expected baseline speed for current time
    /// </summary>
    public int? BaselineSpeed { get; set; }

    /// <summary>
    /// Learning mode active
    /// </summary>
    public bool LearningModeActive { get; set; }

    /// <summary>
    /// Learning mode progress (0-100%)
    /// </summary>
    public double LearningModeProgress { get; set; }

    /// <summary>
    /// Last adjustment timestamp
    /// </summary>
    public DateTime? LastAdjustment { get; set; }

    /// <summary>
    /// Last adjustment reason
    /// </summary>
    public string? LastAdjustmentReason { get; set; }
}
