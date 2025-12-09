# NetworkOptimizer.Sqm - Project Summary

## Overview

NetworkOptimizer.Sqm is a production-ready C# library for generating and managing Smart Queue Management (SQM) scripts for UniFi Cloud Gateway and Dream Machine devices. The system implements adaptive bandwidth management with baseline learning and latency-based rate adjustment.

## Project Structure

```
NetworkOptimizer.Sqm/
├── Models/
│   ├── SqmConfiguration.cs      # Configuration model
│   ├── SqmStatus.cs             # Status model
│   ├── BaselineData.cs          # Baseline table structures
│   └── SpeedtestResult.cs       # Ookla speedtest JSON models
├── SqmManager.cs                # Main orchestrator
├── BaselineCalculator.cs        # 168-hour baseline management
├── SpeedtestIntegration.cs      # Ookla speedtest CLI integration
├── LatencyMonitor.cs            # Ping-based latency monitoring
├── ScriptGenerator.cs           # Shell script generation
├── Examples/
│   └── BasicUsage.cs            # Usage examples
├── Demo/
│   └── Program.cs               # Interactive demo
├── NetworkOptimizer.Sqm.csproj  # Project file
├── README.md                    # Documentation
└── SUMMARY.md                   # This file
```

## Core Components

### 1. SqmManager.cs
**Main orchestrator for SQM operations**

Key Methods:
- `ConfigureSqm(config)` - Configure SQM for a WAN interface
- `StartLearningMode()` - Begin baseline data collection
- `StopLearningMode()` - End learning mode
- `GetStatus()` - Get current SQM status
- `TriggerSpeedtest(json)` - Process speedtest results
- `ApplyRateAdjustment(latency, rate)` - Calculate rate adjustment
- `GenerateScripts()` - Generate deployment scripts
- `ValidateConfiguration()` - Validate configuration

### 2. BaselineCalculator.cs
**168-hour baseline management (7 days × 24 hours)**

Features:
- Per-hour statistics: mean, stddev, min, max, median
- Blending algorithm:
  - 60/40 blend if within 10% of baseline
  - 80/20 blend if >10% below baseline
- Learning progress tracking (0-100%)
- Incremental baseline updates
- Import/export in shell script format

Key Methods:
- `AddSample(sample)` - Add speedtest sample
- `CalculateBaseline()` - Calculate statistics from samples
- `CalculateBlendedSpeed(measured, baseline)` - Blend speeds
- `GetLearningProgress()` - Get completion percentage
- `ExportToShellFormat()` - Export for script generation
- `ImportFromShellFormat(data)` - Load baseline from data

### 3. SpeedtestIntegration.cs
**Ookla Speedtest CLI integration**

Features:
- Parse Ookla speedtest JSON output
- Convert bytes/sec to Mbps
- Calculate effective rate with overhead multiplier (5-15%)
- Apply minimum floor and maximum cap
- Baseline blending for stable rates
- Result validation

Key Methods:
- `ParseSpeedtestJson(json)` - Parse JSON result
- `BytesPerSecToMbps(bytes)` - Convert bandwidth
- `CalculateEffectiveRate(mbps)` - Apply overhead and caps
- `ProcessSpeedtestResult(result, baseline)` - Full processing
- `CreateSample(result)` - Create baseline sample
- `IsValidResult(result)` - Validate result

### 4. LatencyMonitor.cs
**Ping-based latency monitoring and rate adjustment**

Features:
- Threshold detection (baseline + deviation)
- Rate adjustment calculations:
  - High latency: `0.97^n` decrease (3% per deviation)
  - Normal/reduced latency: `1.04^n` increase (4% recovery)
- Automatic recovery to optimal bandwidth
- Configurable ping host and thresholds

Key Methods:
- `CalculateRateAdjustment(latency, rate, baseline)` - Calculate adjustment
- `IsLatencyHigh(latency)` - Check if latency exceeds threshold
- `CalculateDeviationCount(latency)` - Count deviations
- `ParsePingOutput(output)` - Parse ping command output
- `GetRateBounds()` - Get min/optimal/max rates

Adjustment Logic:
```
High Latency (≥ baseline + threshold):
  - Decrease: rate × 0.97^deviations
  - Minimum: 180 Mbps

Reduced Latency (< baseline - 0.4ms):
  - If rate < 92% max: Apply 1.04² (double increase)
  - If rate < 94% max: Normalize to 94%
  - Else: Keep current rate

Normal Latency (within 0.3ms of baseline):
  - If rate < 90% max: Apply 1.04 increase
  - If rate < 92% max: Normalize to 92%
  - Else: Keep current rate
```

### 5. ScriptGenerator.cs
**Shell script generation from templates**

Generated Scripts:
1. **20-sqm-speedtest-setup.sh** - Boot script for speedtest
   - Installs Ookla speedtest CLI
   - Sets up cron jobs
   - Schedules initial calibration

2. **21-sqm-ping-setup.sh** - Boot script for ping monitoring
   - Sets up cron job for latency checks
   - Excludes speedtest times

3. **sqm-speedtest-adjust.sh** - Speedtest execution
   - Runs speedtest on interface
   - Blends with baseline
   - Updates TC classes

4. **sqm-ping-adjust.sh** - Latency monitoring
   - Pings target host
   - Calculates rate adjustment
   - Updates TC classes

5. **install.sh** - Deployment script
   - Copies scripts to device
   - Sets permissions
   - Runs initial setup

6. **sqm-metrics-collector.sh** - InfluxDB metrics (optional)
   - Collects rate, latency, speedtest
   - Sends to InfluxDB

Key Methods:
- `GenerateAllScripts(baseline)` - Generate all scripts
- `GenerateSpeedtestSetupScript(baseline)` - Boot script
- `GeneratePingSetupScript(baseline)` - Boot script
- `GenerateSpeedtestAdjustScript(baseline)` - Main logic
- `GeneratePingAdjustScript(baseline)` - Main logic
- `GenerateInstallScript()` - Deployment
- `GenerateMetricsCollectorScript()` - Metrics

## Algorithm Details

### Baseline Learning

The system learns typical speeds across 168 hours (7 days × 24 hours):

1. **Collection Phase**
   - Collect speedtest samples over time
   - Group by day-of-week (0=Mon, 6=Sun) and hour (0-23)
   - Store with timestamp

2. **Statistical Analysis**
   - Calculate mean, stddev, min, max, median per hour
   - Median used as baseline value
   - Track sample count and last update

3. **Blending Strategy**
   ```
   threshold = baseline × 0.9

   if measured ≥ threshold:
       blended = baseline × 0.6 + measured × 0.4  (60/40)
   else:
       blended = baseline × 0.8 + measured × 0.2  (80/20)
   ```

4. **Overhead Application**
   - Apply 5-15% overhead multiplier
   - Cap at maximum speed
   - Apply 95% safety cap

### Latency-Based Adjustment

Continuous adjustment based on ping latency:

1. **High Latency Response**
   ```
   deviations = (latency - baseline) / threshold
   multiplier = 0.97^deviations
   new_rate = current_rate × multiplier
   new_rate = max(new_rate, 180)  // minimum floor
   ```

2. **Recovery (Reduced/Normal Latency)**
   ```
   if latency < baseline - 0.4:
       if rate < 92% max: rate × 1.04²
       elif rate < 94% max: normalize to 94%
       else: keep current

   elif latency within 0.3ms of baseline:
       if rate < 90% max: rate × 1.04
       elif rate < 92% max: normalize to 92%
       else: keep current
   ```

3. **Safety Caps**
   - Always cap at 95% of absolute max
   - Never exceed configured max speed
   - Never go below 180 Mbps

## Configuration Parameters

```csharp
SqmConfiguration {
    // Interface
    Interface               // WAN interface (e.g., "eth2")

    // Speed limits
    MaxDownloadSpeed        // Maximum ceiling (e.g., 285 Mbps)
    MinDownloadSpeed        // Minimum floor (e.g., 190 Mbps)
    AbsoluteMaxDownloadSpeed // Absolute max (e.g., 280 Mbps)
    OverheadMultiplier      // Overhead (1.05 = 5%)

    // Latency monitoring
    PingHost                // Target for ping (IP or hostname)
    BaselineLatency         // Optimal latency (e.g., 17.9 ms)
    LatencyThreshold        // Deviation threshold (e.g., 2.2 ms)
    LatencyDecrease         // Decrease multiplier (e.g., 0.97)
    LatencyIncrease         // Increase multiplier (e.g., 1.04)

    // Scheduling
    SpeedtestSchedule       // Cron expressions (e.g., ["0 6 * * *"])
    PingAdjustmentInterval  // Minutes between adjustments (e.g., 5)

    // Learning mode
    LearningMode            // Enable baseline learning
    LearningModeStarted     // When learning started

    // InfluxDB (optional)
    InfluxDbEndpoint        // InfluxDB URL
    InfluxDbToken          // Auth token
    InfluxDbOrg            // Organization
    InfluxDbBucket         // Bucket name
}
```

## Usage Examples

### Basic Setup
```csharp
var config = new SqmConfiguration {
    Interface = "eth2",
    MaxDownloadSpeed = 285,
    MinDownloadSpeed = 190,
    PingHost = "40.134.217.121",
    BaselineLatency = 17.9,
    LatencyThreshold = 2.2
};

var manager = new SqmManager(config);
manager.GenerateScriptsToDirectory("./sqm-scripts");
```

### Learning Mode
```csharp
manager.StartLearningMode();

// Collect speedtest samples over time
await manager.TriggerSpeedtest(speedtestJson);

// Check progress
var progress = manager.GetLearningProgress(); // 0-100%

if (manager.IsLearningComplete()) {
    manager.StopLearningMode();
}
```

### Process Speedtest
```csharp
var speedtestJson = await RunSpeedtest();
var effectiveRate = await manager.TriggerSpeedtest(speedtestJson);
Console.WriteLine($"Set rate to {effectiveRate} Mbps");
```

### Monitor Latency
```csharp
var latency = MeasureLatency(); // from ping
var currentRate = GetCurrentRate(); // from TC

var (newRate, reason) = manager.ApplyRateAdjustment(latency, currentRate);
Console.WriteLine($"Adjusted to {newRate} Mbps: {reason}");
```

## Deployment Workflow

1. **Generate Scripts**
   ```csharp
   manager.GenerateScriptsToDirectory("./sqm-scripts");
   ```

2. **Copy to Device**
   ```bash
   scp -r ./sqm-scripts/* root@192.168.1.1:/tmp/
   ```

3. **Install**
   ```bash
   ssh root@192.168.1.1
   cd /tmp
   ./install.sh
   ```

4. **Monitor**
   ```bash
   tail -f /var/log/sqm-speedtest-adjust.log
   tail -f /var/log/sqm-ping-adjust.log
   ```

## Key Features

1. **Adaptive Bandwidth Management**
   - Real-time adjustment based on latency
   - Baseline-aware speedtest blending
   - Smooth rate transitions

2. **168-Hour Baseline Learning**
   - Captures weekly patterns
   - Accounts for peak/off-peak times
   - Incremental updates

3. **Production-Ready Scripts**
   - Boot persistence (on-boot.d)
   - Cron-based scheduling
   - Comprehensive logging
   - Error handling

4. **Flexible Configuration**
   - Configurable thresholds
   - Custom schedules
   - Multiple interfaces
   - InfluxDB integration

5. **Safety Mechanisms**
   - Minimum/maximum rate caps
   - 95% safety cap
   - Gradual adjustments
   - Baseline stability

## Dependencies

- .NET 8.0
- System.Text.Json (for JSON parsing)

## Device Requirements

- UniFi Cloud Gateway or Dream Machine
- Debian-based OS
- `tc` (traffic control)
- `jq` (JSON parsing)
- `bc` (bash calculator)
- Internet access for Ookla CLI

## Performance Characteristics

- **Speedtest**: Runs at scheduled times (e.g., 6 AM, 6:30 PM)
- **Ping Adjustment**: Every 5 minutes (configurable)
- **Response Time**: Immediate rate adjustment
- **Baseline Completeness**: 7 days for full 168-hour coverage
- **Memory Usage**: Minimal (bash scripts, TC rules)
- **CPU Usage**: Low (periodic ping/speedtest)

## Logging

All operations logged to:
- `/var/log/sqm-speedtest-adjust.log` - Speedtest results and adjustments
- `/var/log/sqm-ping-adjust.log` - Latency monitoring and adjustments

Log format:
```
[2024-01-15T06:00:00] Download speed measured on eth2: 260 Mbps
[2024-01-15T06:00:01] Adjusted speedtest-based rate to 265 Mbps on eth2
[2024-01-15T06:05:00] SQM Ping Script invoked on eth2
[2024-01-15T06:05:01] Latency is normal: 18.1 ms
[2024-01-15T06:05:02] Adjusted rate to 265.0 Mbps
```

## Future Enhancements

Potential improvements:
- Upload bandwidth management
- Multiple interface support
- Web UI for configuration
- Historical analysis dashboard
- Automatic ISP detection
- Machine learning for prediction
- Anomaly detection
- Integration with UniFi API

## License

Production-ready code for UniFi network optimization.

## Contact

For questions or issues, refer to the main UniFiAnalyzer project documentation.
