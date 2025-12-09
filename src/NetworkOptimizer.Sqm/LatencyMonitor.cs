using NetworkOptimizer.Sqm.Models;

namespace NetworkOptimizer.Sqm;

/// <summary>
/// Monitors latency and calculates rate adjustments based on ping results
/// </summary>
public class LatencyMonitor
{
    private readonly SqmConfiguration _config;

    public LatencyMonitor(SqmConfiguration config)
    {
        _config = config;
    }

    /// <summary>
    /// Calculate rate adjustment based on current latency
    /// </summary>
    /// <param name="currentLatency">Current ping latency in milliseconds</param>
    /// <param name="currentRate">Current download rate in Mbps</param>
    /// <param name="baselineSpeed">Baseline speed for current hour (optional)</param>
    /// <returns>Adjusted rate in Mbps and reason for adjustment</returns>
    public (double adjustedRate, string reason) CalculateRateAdjustment(
        double currentLatency,
        double currentRate,
        double? baselineSpeed = null)
    {
        var thresholdLatency = _config.BaselineLatency + _config.LatencyThreshold;
        var lowLatency = _config.BaselineLatency - 0.4;
        var normalLatencyRange = _config.BaselineLatency + 0.3;

        // High latency detected
        if (currentLatency >= thresholdLatency)
        {
            var deviationCount = (int)Math.Ceiling(
                (currentLatency - _config.BaselineLatency) / _config.LatencyThreshold
            );

            var decreaseMultiplier = Math.Pow(_config.LatencyDecrease, deviationCount);
            var newRate = currentRate * decreaseMultiplier;

            // Enforce minimum rate
            newRate = Math.Max(newRate, 180);

            var reason = $"High latency: {currentLatency:F1}ms (threshold: {thresholdLatency:F1}ms), " +
                        $"decreased by {(1 - decreaseMultiplier) * 100:F1}% ({deviationCount} deviations)";

            return (Math.Round(newRate, 1), reason);
        }

        // Latency is reduced (lower than baseline)
        if (currentLatency < lowLatency)
        {
            var lowerBound = _config.AbsoluteMaxDownloadSpeed * 0.92;
            var midBound = _config.AbsoluteMaxDownloadSpeed * 0.94;

            if (currentRate < lowerBound)
            {
                // Apply double increase
                var newRate = currentRate * _config.LatencyIncrease * _config.LatencyIncrease;
                var reason = $"Latency reduced: {currentLatency:F1}ms, rate significantly below baseline, " +
                            $"applying 2x increase";
                return (CapRate(newRate), reason);
            }
            else if (currentRate < midBound)
            {
                // Normalize to mid bound
                var reason = $"Latency reduced: {currentLatency:F1}ms, normalizing to optimal bandwidth";
                return (CapRate(midBound), reason);
            }
            else
            {
                // Keep current rate
                var reason = $"Latency reduced: {currentLatency:F1}ms, keeping current rate";
                return (CapRate(currentRate), reason);
            }
        }

        // Normal latency
        var lowerBoundNormal = _config.AbsoluteMaxDownloadSpeed * 0.9;
        var midBoundNormal = _config.AbsoluteMaxDownloadSpeed * 0.92;
        var latencyDiff = currentLatency - _config.BaselineLatency;
        var isLatencyNormal = latencyDiff <= 0.3;

        if (currentRate < lowerBoundNormal && isLatencyNormal)
        {
            // Apply increase
            var newRate = currentRate * _config.LatencyIncrease;
            var reason = $"Normal latency: {currentLatency:F1}ms (within 0.3ms), " +
                        $"rate below threshold, applying increase";
            return (CapRate(newRate), reason);
        }
        else if (currentRate < midBoundNormal && isLatencyNormal)
        {
            // Normalize to mid bound
            var reason = $"Normal latency: {currentLatency:F1}ms (within 0.3ms), " +
                        $"normalizing to optimal bandwidth";
            return (CapRate(midBoundNormal), reason);
        }
        else
        {
            // Keep current rate
            var reason = $"Normal latency: {currentLatency:F1}ms, maintaining current rate";
            return (CapRate(currentRate), reason);
        }
    }

    /// <summary>
    /// Detect if latency exceeds threshold
    /// </summary>
    public bool IsLatencyHigh(double currentLatency)
    {
        return currentLatency >= (_config.BaselineLatency + _config.LatencyThreshold);
    }

    /// <summary>
    /// Calculate number of standard deviations from baseline
    /// </summary>
    public int CalculateDeviationCount(double currentLatency)
    {
        return (int)Math.Ceiling(
            (currentLatency - _config.BaselineLatency) / _config.LatencyThreshold
        );
    }

    /// <summary>
    /// Generate ping command for shell script
    /// </summary>
    public string GeneratePingCommand()
    {
        return $"ping -I {_config.Interface} -c 20 -i 0.25 -q \"{_config.PingHost}\"";
    }

    /// <summary>
    /// Parse ping output to extract average latency
    /// </summary>
    /// <param name="pingOutput">Output from ping command</param>
    /// <returns>Average latency in milliseconds, or null if parsing failed</returns>
    public double? ParsePingOutput(string pingOutput)
    {
        // Expected format: rtt min/avg/max/mdev = 10.123/12.456/15.789/2.345 ms
        var lines = pingOutput.Split('\n');
        var rttLine = lines.FirstOrDefault(l => l.Contains("rtt min/avg/max"));

        if (rttLine == null) return null;

        try
        {
            var parts = rttLine.Split('=')[1].Trim().Split('/');
            if (parts.Length >= 2)
            {
                return double.Parse(parts[1]);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Calculate exponential decrease multiplier
    /// </summary>
    public double CalculateDecreaseMultiplier(int deviations)
    {
        return Math.Pow(_config.LatencyDecrease, deviations);
    }

    /// <summary>
    /// Calculate exponential increase multiplier
    /// </summary>
    public double CalculateIncreaseMultiplier(int steps = 1)
    {
        return Math.Pow(_config.LatencyIncrease, steps);
    }

    /// <summary>
    /// Cap rate at maximum allowed value
    /// </summary>
    private double CapRate(double rate)
    {
        // Apply 95% safety cap
        var safetyCapRate = _config.AbsoluteMaxDownloadSpeed * 0.95;
        rate = Math.Min(rate, safetyCapRate);

        // Apply absolute maximum
        rate = Math.Min(rate, _config.MaxDownloadSpeed);

        return Math.Round(rate, 1);
    }

    /// <summary>
    /// Check if current rate needs recovery (increase)
    /// </summary>
    public bool NeedsRecovery(double currentRate)
    {
        var recoveryThreshold = _config.AbsoluteMaxDownloadSpeed * 0.92;
        return currentRate < recoveryThreshold;
    }

    /// <summary>
    /// Calculate recommended rate bounds for current configuration
    /// </summary>
    public (double minRate, double optimalRate, double maxRate) GetRateBounds()
    {
        return (
            minRate: 180.0,
            optimalRate: _config.AbsoluteMaxDownloadSpeed * 0.94,
            maxRate: _config.AbsoluteMaxDownloadSpeed * 0.95
        );
    }
}
