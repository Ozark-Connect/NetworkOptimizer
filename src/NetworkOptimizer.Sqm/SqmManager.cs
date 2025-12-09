using NetworkOptimizer.Sqm.Models;

namespace NetworkOptimizer.Sqm;

/// <summary>
/// Main orchestrator for SQM (Smart Queue Management) configuration and operations
/// </summary>
public class SqmManager
{
    private readonly SqmConfiguration _config;
    private readonly BaselineCalculator _baselineCalculator;
    private readonly SpeedtestIntegration _speedtestIntegration;
    private readonly LatencyMonitor _latencyMonitor;
    private readonly ScriptGenerator _scriptGenerator;

    private SqmStatus _currentStatus = new();

    public SqmManager(SqmConfiguration config)
    {
        _config = config;
        _baselineCalculator = new BaselineCalculator();
        _speedtestIntegration = new SpeedtestIntegration(config);
        _latencyMonitor = new LatencyMonitor(config);
        _scriptGenerator = new ScriptGenerator(config);
    }

    /// <summary>
    /// Configure SQM for a WAN interface
    /// </summary>
    public void ConfigureSqm(SqmConfiguration config)
    {
        // Update configuration
        _config.Interface = config.Interface;
        _config.MaxDownloadSpeed = config.MaxDownloadSpeed;
        _config.MinDownloadSpeed = config.MinDownloadSpeed;
        _config.AbsoluteMaxDownloadSpeed = config.AbsoluteMaxDownloadSpeed;
        _config.OverheadMultiplier = config.OverheadMultiplier;
        _config.PingHost = config.PingHost;
        _config.BaselineLatency = config.BaselineLatency;
        _config.LatencyThreshold = config.LatencyThreshold;
        _config.LatencyDecrease = config.LatencyDecrease;
        _config.LatencyIncrease = config.LatencyIncrease;
    }

    /// <summary>
    /// Start learning mode to collect baseline data
    /// </summary>
    public void StartLearningMode()
    {
        _config.LearningMode = true;
        _config.LearningModeStarted = DateTime.Now;

        _currentStatus.LearningModeActive = true;
        _currentStatus.LearningModeProgress = _baselineCalculator.GetLearningProgress();
    }

    /// <summary>
    /// Stop learning mode
    /// </summary>
    public void StopLearningMode()
    {
        _config.LearningMode = false;
        _currentStatus.LearningModeActive = false;
        _currentStatus.LearningModeProgress = 100.0;

        // Calculate final baseline
        _baselineCalculator.CalculateBaseline();
    }

    /// <summary>
    /// Get current SQM status
    /// </summary>
    public SqmStatus GetStatus()
    {
        _currentStatus.LearningModeActive = _config.LearningMode;
        _currentStatus.LearningModeProgress = _baselineCalculator.GetLearningProgress();
        _currentStatus.BaselineSpeed = _baselineCalculator.GetCurrentBaselineSpeed();

        return _currentStatus;
    }

    /// <summary>
    /// Trigger manual speedtest and apply results
    /// </summary>
    public async Task<double> TriggerSpeedtest(string speedtestJsonOutput)
    {
        var result = _speedtestIntegration.ParseSpeedtestJson(speedtestJsonOutput);
        if (result == null || !_speedtestIntegration.IsValidResult(result))
        {
            throw new InvalidOperationException("Invalid speedtest result");
        }

        // Create sample for baseline
        var sample = _speedtestIntegration.CreateSample(result);

        // Update baseline if in learning mode
        if (_config.LearningMode)
        {
            _baselineCalculator.UpdateHourlyBaseline(sample);
        }

        // Calculate effective rate
        var effectiveRate = _speedtestIntegration.ProcessSpeedtestResult(result, _baselineCalculator);

        // Update status
        _currentStatus.LastSpeedtest = _speedtestIntegration.BytesPerSecToMbps(result.Download.Bandwidth);
        _currentStatus.LastSpeedtestTime = result.Timestamp;
        _currentStatus.CurrentRate = effectiveRate;
        _currentStatus.LastAdjustment = DateTime.Now;
        _currentStatus.LastAdjustmentReason = $"Speedtest: {_currentStatus.LastSpeedtest:F0} Mbps → {effectiveRate:F0} Mbps";

        return effectiveRate;
    }

    /// <summary>
    /// Apply rate adjustment based on current latency
    /// </summary>
    public (double adjustedRate, string reason) ApplyRateAdjustment(double currentLatency, double currentRate)
    {
        var baselineSpeed = _baselineCalculator.GetCurrentBaselineSpeed();
        var (adjustedRate, reason) = _latencyMonitor.CalculateRateAdjustment(
            currentLatency,
            currentRate,
            baselineSpeed
        );

        // Update status
        _currentStatus.CurrentLatency = currentLatency;
        _currentStatus.CurrentRate = adjustedRate;
        _currentStatus.LastAdjustment = DateTime.Now;
        _currentStatus.LastAdjustmentReason = reason;

        return (adjustedRate, reason);
    }

    /// <summary>
    /// Load baseline data from file or database
    /// </summary>
    public void LoadBaseline(BaselineTable baseline)
    {
        _baselineCalculator.LoadBaselineTable(baseline);
    }

    /// <summary>
    /// Get current baseline table
    /// </summary>
    public BaselineTable GetBaselineTable()
    {
        return _baselineCalculator.GetBaselineTable();
    }

    /// <summary>
    /// Export baseline to shell script format
    /// </summary>
    public Dictionary<string, string> ExportBaselineForScript()
    {
        return _baselineCalculator.ExportToShellFormat();
    }

    /// <summary>
    /// Generate all shell scripts for deployment
    /// </summary>
    public Dictionary<string, string> GenerateScripts()
    {
        var baseline = ExportBaselineForScript();
        return _scriptGenerator.GenerateAllScripts(baseline);
    }

    /// <summary>
    /// Generate shell scripts and save to directory
    /// </summary>
    public void GenerateScriptsToDirectory(string outputDirectory)
    {
        var scripts = GenerateScripts();

        Directory.CreateDirectory(outputDirectory);

        foreach (var (filename, content) in scripts)
        {
            var filePath = Path.Combine(outputDirectory, filename);
            File.WriteAllText(filePath, content);
        }
    }

    /// <summary>
    /// Check if learning mode is complete
    /// </summary>
    public bool IsLearningComplete()
    {
        return _baselineCalculator.IsLearningComplete();
    }

    /// <summary>
    /// Get learning mode progress (0-100%)
    /// </summary>
    public double GetLearningProgress()
    {
        return _baselineCalculator.GetLearningProgress();
    }

    /// <summary>
    /// Get recommended rate bounds
    /// </summary>
    public (double minRate, double optimalRate, double maxRate) GetRateBounds()
    {
        return _latencyMonitor.GetRateBounds();
    }

    /// <summary>
    /// Validate configuration
    /// </summary>
    public List<string> ValidateConfiguration()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(_config.Interface))
        {
            errors.Add("Interface is required");
        }

        if (_config.MaxDownloadSpeed <= 0)
        {
            errors.Add("MaxDownloadSpeed must be greater than 0");
        }

        if (_config.MinDownloadSpeed <= 0)
        {
            errors.Add("MinDownloadSpeed must be greater than 0");
        }

        if (_config.MinDownloadSpeed >= _config.MaxDownloadSpeed)
        {
            errors.Add("MinDownloadSpeed must be less than MaxDownloadSpeed");
        }

        if (_config.AbsoluteMaxDownloadSpeed < _config.MaxDownloadSpeed)
        {
            errors.Add("AbsoluteMaxDownloadSpeed should be greater than or equal to MaxDownloadSpeed");
        }

        if (_config.OverheadMultiplier < 1.0 || _config.OverheadMultiplier > 1.2)
        {
            errors.Add("OverheadMultiplier should be between 1.0 and 1.2 (0-20% overhead)");
        }

        if (string.IsNullOrWhiteSpace(_config.PingHost))
        {
            errors.Add("PingHost is required");
        }

        if (_config.BaselineLatency <= 0)
        {
            errors.Add("BaselineLatency must be greater than 0");
        }

        if (_config.LatencyThreshold <= 0)
        {
            errors.Add("LatencyThreshold must be greater than 0");
        }

        if (_config.LatencyDecrease <= 0 || _config.LatencyDecrease >= 1.0)
        {
            errors.Add("LatencyDecrease should be between 0 and 1.0 (e.g., 0.97 for 3% decrease)");
        }

        if (_config.LatencyIncrease <= 1.0 || _config.LatencyIncrease > 1.2)
        {
            errors.Add("LatencyIncrease should be between 1.0 and 1.2 (e.g., 1.04 for 4% increase)");
        }

        if (_config.PingAdjustmentInterval < 1)
        {
            errors.Add("PingAdjustmentInterval must be at least 1 minute");
        }

        return errors;
    }
}
