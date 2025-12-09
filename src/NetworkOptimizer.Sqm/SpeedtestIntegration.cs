using System.Text.Json;
using NetworkOptimizer.Sqm.Models;

namespace NetworkOptimizer.Sqm;

/// <summary>
/// Integrates with Ookla Speedtest CLI and processes results
/// </summary>
public class SpeedtestIntegration
{
    private readonly SqmConfiguration _config;

    public SpeedtestIntegration(SqmConfiguration config)
    {
        _config = config;
    }

    /// <summary>
    /// Parse Ookla speedtest JSON output
    /// </summary>
    public SpeedtestResult? ParseSpeedtestJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SpeedtestResult>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Convert bandwidth from bytes/sec to Mbps
    /// </summary>
    public double BytesPerSecToMbps(long bytesPerSec)
    {
        return (bytesPerSec * 8.0) / 1_000_000.0;
    }

    /// <summary>
    /// Calculate effective download rate with overhead multiplier
    /// </summary>
    /// <param name="downloadMbps">Raw download speed in Mbps</param>
    /// <returns>Adjusted download speed in Mbps</returns>
    public double CalculateEffectiveRate(double downloadMbps)
    {
        // Apply overhead multiplier (typically 5-15%)
        var effectiveRate = downloadMbps * _config.OverheadMultiplier;

        // Apply minimum floor
        effectiveRate = Math.Max(effectiveRate, _config.MinDownloadSpeed);

        // Apply maximum cap
        effectiveRate = Math.Min(effectiveRate, _config.MaxDownloadSpeed);

        return Math.Round(effectiveRate, 0);
    }

    /// <summary>
    /// Process speedtest result and calculate effective rate with baseline blending
    /// </summary>
    public double ProcessSpeedtestResult(SpeedtestResult result, BaselineCalculator baselineCalculator)
    {
        // Convert bytes/sec to Mbps
        var downloadMbps = BytesPerSecToMbps(result.Download.Bandwidth);

        // Apply minimum floor before blending
        downloadMbps = Math.Max(downloadMbps, _config.MinDownloadSpeed);

        // Get current baseline
        var baselineSpeed = baselineCalculator.GetCurrentBaselineSpeed();

        double blendedSpeed;
        if (baselineSpeed.HasValue)
        {
            // Blend with baseline
            blendedSpeed = baselineCalculator.CalculateBlendedSpeed(
                downloadMbps,
                baselineSpeed.Value,
                thresholdPercent: 0.1
            );
        }
        else
        {
            // No baseline available, use measured speed
            blendedSpeed = downloadMbps;
        }

        // Apply overhead multiplier
        var effectiveRate = blendedSpeed * _config.OverheadMultiplier;

        // Apply maximum cap
        effectiveRate = Math.Min(effectiveRate, _config.MaxDownloadSpeed);

        // Apply 95% safety cap
        var safetyCapRate = _config.MaxDownloadSpeed * 0.95;
        effectiveRate = Math.Min(effectiveRate, safetyCapRate);

        return Math.Round(effectiveRate, 0);
    }

    /// <summary>
    /// Create speedtest sample from result for baseline calculation
    /// </summary>
    public SpeedtestSample CreateSample(SpeedtestResult result)
    {
        var downloadMbps = BytesPerSecToMbps(result.Download.Bandwidth);
        var uploadMbps = BytesPerSecToMbps(result.Upload.Bandwidth);

        var now = DateTime.Now;
        return new SpeedtestSample
        {
            Timestamp = result.Timestamp,
            DayOfWeek = GetDayOfWeek(now),
            Hour = now.Hour,
            DownloadSpeed = downloadMbps,
            UploadSpeed = uploadMbps,
            Latency = result.Ping.Latency
        };
    }

    /// <summary>
    /// Validate speedtest result
    /// </summary>
    public bool IsValidResult(SpeedtestResult result)
    {
        if (result == null) return false;
        if (result.Download.Bandwidth <= 0) return false;
        if (result.Upload.Bandwidth <= 0) return false;
        if (result.Ping.Latency <= 0) return false;

        // Check for reasonable values
        var downloadMbps = BytesPerSecToMbps(result.Download.Bandwidth);
        if (downloadMbps < 1 || downloadMbps > 10000) return false;

        return true;
    }

    /// <summary>
    /// Generate speedtest command for shell script
    /// </summary>
    public string GenerateSpeedtestCommand()
    {
        return $"speedtest --accept-license --format=json --interface={_config.Interface}";
    }

    /// <summary>
    /// Calculate variance from baseline as percentage
    /// </summary>
    public double CalculateVariancePercent(double measuredSpeed, double baselineSpeed)
    {
        if (baselineSpeed == 0) return 0;
        return ((measuredSpeed - baselineSpeed) / baselineSpeed) * 100.0;
    }

    /// <summary>
    /// Determine blend ratio based on variance from baseline
    /// </summary>
    /// <param name="variancePercent">Variance from baseline as percentage</param>
    /// <returns>Tuple of (baselineWeight, measuredWeight)</returns>
    public (double baselineWeight, double measuredWeight) DetermineBlendRatio(double variancePercent)
    {
        if (variancePercent >= -10)
        {
            // Within 10% of baseline: 60/40 blend
            return (0.6, 0.4);
        }
        else
        {
            // More than 10% below baseline: 80/20 blend
            return (0.8, 0.2);
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
