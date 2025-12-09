using NetworkOptimizer.Sqm.Models;

namespace NetworkOptimizer.Sqm;

/// <summary>
/// Calculates and manages baseline speed data across 168 hours (7 days x 24 hours)
/// </summary>
public class BaselineCalculator
{
    private readonly List<SpeedtestSample> _samples = new();
    private BaselineTable _baselineTable = new();

    /// <summary>
    /// Add a speedtest sample to the collection
    /// </summary>
    public void AddSample(SpeedtestSample sample)
    {
        _samples.Add(sample);
    }

    /// <summary>
    /// Add a speedtest sample from current time
    /// </summary>
    public void AddSample(double downloadSpeed, double uploadSpeed, double latency)
    {
        var now = DateTime.Now;
        var sample = new SpeedtestSample
        {
            Timestamp = now,
            DayOfWeek = GetDayOfWeek(now),
            Hour = now.Hour,
            DownloadSpeed = downloadSpeed,
            UploadSpeed = uploadSpeed,
            Latency = latency
        };
        AddSample(sample);
    }

    /// <summary>
    /// Calculate baseline statistics from collected samples
    /// </summary>
    public BaselineTable CalculateBaseline()
    {
        var baseline = new BaselineTable
        {
            CollectionStarted = _samples.MinBy(s => s.Timestamp)?.Timestamp ?? DateTime.Now,
            LastUpdated = DateTime.Now
        };

        // Group samples by day and hour
        var grouped = _samples
            .GroupBy(s => new { s.DayOfWeek, s.Hour })
            .ToDictionary(
                g => $"{g.Key.DayOfWeek}_{g.Key.Hour}",
                g => g.ToList()
            );

        foreach (var (key, samples) in grouped)
        {
            if (samples.Count == 0) continue;

            var speeds = samples.Select(s => s.DownloadSpeed).OrderBy(s => s).ToList();
            var mean = speeds.Average();
            var variance = speeds.Sum(s => Math.Pow(s - mean, 2)) / speeds.Count;
            var stdDev = Math.Sqrt(variance);
            var median = CalculateMedian(speeds);

            var parts = key.Split('_');
            var hourlyBaseline = new HourlyBaseline
            {
                DayOfWeek = int.Parse(parts[0]),
                Hour = int.Parse(parts[1]),
                Mean = mean,
                StdDev = stdDev,
                Min = speeds.First(),
                Max = speeds.Last(),
                Median = median,
                SampleCount = samples.Count,
                LastUpdated = DateTime.Now
            };

            baseline.Baselines[key] = hourlyBaseline;
        }

        baseline.IsComplete = baseline.Baselines.Count == 168;
        _baselineTable = baseline;

        return baseline;
    }

    /// <summary>
    /// Get the current baseline table
    /// </summary>
    public BaselineTable GetBaselineTable() => _baselineTable;

    /// <summary>
    /// Load baseline table from existing data
    /// </summary>
    public void LoadBaselineTable(BaselineTable table)
    {
        _baselineTable = table;
    }

    /// <summary>
    /// Calculate blended speed using baseline and measured speed
    /// </summary>
    /// <param name="measuredSpeed">Speed from speedtest in Mbps</param>
    /// <param name="baselineSpeed">Baseline speed for current hour in Mbps</param>
    /// <param name="thresholdPercent">Threshold percentage (default 0.1 = 10%)</param>
    /// <returns>Blended speed in Mbps</returns>
    public double CalculateBlendedSpeed(double measuredSpeed, double baselineSpeed, double thresholdPercent = 0.1)
    {
        var threshold = baselineSpeed * (1.0 - thresholdPercent);

        if (measuredSpeed >= threshold)
        {
            // Within threshold: 60/40 blend (favor baseline)
            return (baselineSpeed * 0.6) + (measuredSpeed * 0.4);
        }
        else
        {
            // Below threshold: 80/20 blend (heavily favor baseline)
            return (baselineSpeed * 0.8) + (measuredSpeed * 0.2);
        }
    }

    /// <summary>
    /// Get learning mode progress (0-100%)
    /// </summary>
    public double GetLearningProgress()
    {
        return _baselineTable.GetCompletionPercentage();
    }

    /// <summary>
    /// Check if learning mode is complete (all 168 hours have data)
    /// </summary>
    public bool IsLearningComplete()
    {
        return _baselineTable.IsComplete;
    }

    /// <summary>
    /// Get expected baseline speed for current time
    /// </summary>
    public int? GetCurrentBaselineSpeed()
    {
        var baseline = _baselineTable.GetCurrentBaseline();
        return baseline != null ? (int)Math.Round(baseline.Median) : null;
    }

    /// <summary>
    /// Get expected baseline speed for specific time
    /// </summary>
    public int? GetBaselineSpeed(DateTime time)
    {
        var baseline = _baselineTable.GetBaseline(time);
        return baseline != null ? (int)Math.Round(baseline.Median) : null;
    }

    /// <summary>
    /// Update a single hourly baseline with new sample (incremental learning)
    /// </summary>
    public void UpdateHourlyBaseline(SpeedtestSample sample)
    {
        var key = $"{sample.DayOfWeek}_{sample.Hour}";

        if (!_baselineTable.Baselines.TryGetValue(key, out var existing))
        {
            // Create new baseline entry
            existing = new HourlyBaseline
            {
                DayOfWeek = sample.DayOfWeek,
                Hour = sample.Hour,
                Mean = sample.DownloadSpeed,
                Median = sample.DownloadSpeed,
                Min = sample.DownloadSpeed,
                Max = sample.DownloadSpeed,
                StdDev = 0,
                SampleCount = 1,
                LastUpdated = sample.Timestamp
            };
            _baselineTable.Baselines[key] = existing;
        }
        else
        {
            // Update existing baseline with exponential moving average
            var alpha = 0.2; // Weight for new sample
            existing.Mean = (alpha * sample.DownloadSpeed) + ((1 - alpha) * existing.Mean);
            existing.Median = (alpha * sample.DownloadSpeed) + ((1 - alpha) * existing.Median);
            existing.Min = Math.Min(existing.Min, sample.DownloadSpeed);
            existing.Max = Math.Max(existing.Max, sample.DownloadSpeed);
            existing.SampleCount++;
            existing.LastUpdated = sample.Timestamp;

            // Update standard deviation (simplified)
            var variance = Math.Pow(sample.DownloadSpeed - existing.Mean, 2);
            existing.StdDev = Math.Sqrt((existing.StdDev * existing.StdDev * 0.8) + (variance * 0.2));
        }

        _baselineTable.LastUpdated = sample.Timestamp;
        _baselineTable.IsComplete = _baselineTable.Baselines.Count == 168;
    }

    /// <summary>
    /// Create a baseline table from shell script format (for script generation)
    /// </summary>
    public Dictionary<string, string> ExportToShellFormat()
    {
        var result = new Dictionary<string, string>();

        foreach (var (key, baseline) in _baselineTable.Baselines.OrderBy(b => b.Key))
        {
            result[key] = ((int)Math.Round(baseline.Median)).ToString();
        }

        return result;
    }

    /// <summary>
    /// Import baseline from shell script format
    /// </summary>
    public void ImportFromShellFormat(Dictionary<string, string> shellBaseline)
    {
        _baselineTable = new BaselineTable
        {
            CollectionStarted = DateTime.Now,
            LastUpdated = DateTime.Now
        };

        foreach (var (key, value) in shellBaseline)
        {
            var parts = key.Split('_');
            if (parts.Length != 2) continue;

            if (!int.TryParse(parts[0], out var dayOfWeek)) continue;
            if (!int.TryParse(parts[1], out var hour)) continue;
            if (!int.TryParse(value, out var speed)) continue;

            var baseline = new HourlyBaseline
            {
                DayOfWeek = dayOfWeek,
                Hour = hour,
                Mean = speed,
                Median = speed,
                Min = speed,
                Max = speed,
                StdDev = 0,
                SampleCount = 1,
                LastUpdated = DateTime.Now
            };

            _baselineTable.Baselines[key] = baseline;
        }

        _baselineTable.IsComplete = _baselineTable.Baselines.Count == 168;
    }

    /// <summary>
    /// Calculate median from a sorted list of values
    /// </summary>
    private static double CalculateMedian(List<double> sortedValues)
    {
        if (sortedValues.Count == 0) return 0;
        if (sortedValues.Count == 1) return sortedValues[0];

        var mid = sortedValues.Count / 2;
        if (sortedValues.Count % 2 == 0)
        {
            return (sortedValues[mid - 1] + sortedValues[mid]) / 2.0;
        }
        else
        {
            return sortedValues[mid];
        }
    }

    /// <summary>
    /// Convert DateTime to day of week (0 = Monday, 6 = Sunday)
    /// </summary>
    private static int GetDayOfWeek(DateTime time)
    {
        return time.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)time.DayOfWeek - 1;
    }
}
