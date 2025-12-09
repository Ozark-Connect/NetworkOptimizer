namespace NetworkOptimizer.Sqm.Models;

/// <summary>
/// Baseline speed data for a specific hour
/// </summary>
public class HourlyBaseline
{
    /// <summary>
    /// Day of week (0 = Monday, 6 = Sunday)
    /// </summary>
    public int DayOfWeek { get; set; }

    /// <summary>
    /// Hour of day (0-23)
    /// </summary>
    public int Hour { get; set; }

    /// <summary>
    /// Mean speed in Mbps
    /// </summary>
    public double Mean { get; set; }

    /// <summary>
    /// Standard deviation in Mbps
    /// </summary>
    public double StdDev { get; set; }

    /// <summary>
    /// Minimum observed speed in Mbps
    /// </summary>
    public double Min { get; set; }

    /// <summary>
    /// Maximum observed speed in Mbps
    /// </summary>
    public double Max { get; set; }

    /// <summary>
    /// Median speed in Mbps (used for baseline)
    /// </summary>
    public double Median { get; set; }

    /// <summary>
    /// Number of samples collected
    /// </summary>
    public int SampleCount { get; set; }

    /// <summary>
    /// Last updated timestamp
    /// </summary>
    public DateTime LastUpdated { get; set; }
}

/// <summary>
/// Complete baseline table (168 hours = 7 days x 24 hours)
/// </summary>
public class BaselineTable
{
    /// <summary>
    /// Baseline data indexed by "day_hour" (e.g., "0_6" for Monday 6 AM)
    /// </summary>
    public Dictionary<string, HourlyBaseline> Baselines { get; set; } = new();

    /// <summary>
    /// When the baseline collection started
    /// </summary>
    public DateTime CollectionStarted { get; set; }

    /// <summary>
    /// Last time the baseline was updated
    /// </summary>
    public DateTime LastUpdated { get; set; }

    /// <summary>
    /// Is the baseline complete (all 168 hours have data)?
    /// </summary>
    public bool IsComplete { get; set; }

    /// <summary>
    /// Get baseline for a specific time
    /// </summary>
    public HourlyBaseline? GetBaseline(DateTime time)
    {
        var dayOfWeek = GetDayOfWeek(time);
        var hour = time.Hour;
        var key = $"{dayOfWeek}_{hour}";
        return Baselines.TryGetValue(key, out var baseline) ? baseline : null;
    }

    /// <summary>
    /// Get baseline for current time
    /// </summary>
    public HourlyBaseline? GetCurrentBaseline()
    {
        return GetBaseline(DateTime.Now);
    }

    /// <summary>
    /// Convert DateTime to day of week (0 = Monday, 6 = Sunday)
    /// </summary>
    private static int GetDayOfWeek(DateTime time)
    {
        // .NET DayOfWeek: Sunday = 0, Monday = 1
        // We want: Monday = 0, Sunday = 6
        return time.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)time.DayOfWeek - 1;
    }

    /// <summary>
    /// Calculate completion percentage (0-100)
    /// </summary>
    public double GetCompletionPercentage()
    {
        return (Baselines.Count / 168.0) * 100.0;
    }
}

/// <summary>
/// Raw speedtest sample for baseline calculation
/// </summary>
public class SpeedtestSample
{
    public DateTime Timestamp { get; set; }
    public int DayOfWeek { get; set; }
    public int Hour { get; set; }
    public double DownloadSpeed { get; set; }
    public double UploadSpeed { get; set; }
    public double Latency { get; set; }
}
