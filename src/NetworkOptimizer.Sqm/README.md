# NetworkOptimizer.Sqm

Production-ready Smart Queue Management (SQM) system for UniFi Cloud Gateway and Dream Machine devices. This library generates shell scripts that implement adaptive bandwidth management with baseline learning and latency-based rate adjustment.

## Features

### Core Components

1. **SqmManager** - Main orchestrator for SQM operations
   - Configure SQM for WAN interfaces
   - Start/stop learning mode
   - Get current SQM status
   - Trigger manual speedtests
   - Apply rate adjustments

2. **BaselineCalculator** - 168-hour baseline management (7 days × 24 hours)
   - Calculate per-hour statistics (mean, stddev, min, max, median)
   - Blending algorithm: 60/40 or 80/20 based on variance from baseline
   - Learning mode progress tracking
   - Incremental baseline updates

3. **SpeedtestIntegration** - Ookla Speedtest CLI integration
   - Parse JSON speedtest output
   - Calculate effective rate with overhead multiplier (5-15%)
   - Apply minimum floor and maximum cap
   - Baseline blending for stable rate recommendations

4. **LatencyMonitor** - Ping-based latency monitoring
   - Threshold detection (baseline + deviation)
   - Rate adjustment calculations:
     - High latency: 0.97^n decrease (3% per deviation)
     - Normal/reduced latency: 1.04^n increase (4% recovery)
   - Automatic recovery to optimal bandwidth

5. **ScriptGenerator** - Shell script generation
   - Generate deployment scripts from templates
   - Parameterize: interface, max speed, baseline table, InfluxDB endpoint
   - Generate install.sh for easy deployment

## Usage

### Basic Configuration

```csharp
using NetworkOptimizer.Sqm;
using NetworkOptimizer.Sqm.Models;

// Create configuration
var config = new SqmConfiguration
{
    Interface = "eth2",
    MaxDownloadSpeed = 285,
    MinDownloadSpeed = 190,
    AbsoluteMaxDownloadSpeed = 280,
    OverheadMultiplier = 1.05, // 5% overhead
    PingHost = "40.134.217.121",
    BaselineLatency = 17.9,
    LatencyThreshold = 2.2,
    LatencyDecrease = 0.97, // 3% decrease
    LatencyIncrease = 1.04, // 4% increase
    SpeedtestSchedule = new List<string> { "0 6 * * *", "30 18 * * *" },
    PingAdjustmentInterval = 5
};

// Create manager
var manager = new SqmManager(config);
```

### Learning Mode

```csharp
// Start learning mode to collect baseline data
manager.StartLearningMode();

// Check progress
var progress = manager.GetLearningProgress();
Console.WriteLine($"Learning progress: {progress:F1}%");

// Stop learning mode when complete
if (manager.IsLearningComplete())
{
    manager.StopLearningMode();
}
```

### Process Speedtest Results

```csharp
// After running speedtest on device
string speedtestJson = File.ReadAllText("speedtest-result.json");

// Process and apply
var effectiveRate = await manager.TriggerSpeedtest(speedtestJson);
Console.WriteLine($"Effective rate: {effectiveRate} Mbps");
```

### Monitor Latency and Adjust

```csharp
// Get current latency (from ping)
double currentLatency = 19.5; // ms
double currentRate = 265; // Mbps

// Calculate adjustment
var (adjustedRate, reason) = manager.ApplyRateAdjustment(currentLatency, currentRate);
Console.WriteLine($"Adjusted to {adjustedRate} Mbps: {reason}");
```

### Generate Scripts for Deployment

```csharp
// Generate all scripts
var scripts = manager.GenerateScripts();

// Save to directory
manager.GenerateScriptsToDirectory("/path/to/output");

// Deploy to UniFi device:
// 1. Copy scripts to device
// 2. Run install.sh
```

### Get Status

```csharp
var status = manager.GetStatus();
Console.WriteLine($"Current Rate: {status.CurrentRate} Mbps");
Console.WriteLine($"Last Speedtest: {status.LastSpeedtest} Mbps at {status.LastSpeedtestTime}");
Console.WriteLine($"Current Latency: {status.CurrentLatency} ms");
Console.WriteLine($"Baseline Speed: {status.BaselineSpeed} Mbps");
Console.WriteLine($"Learning Mode: {status.LearningModeActive} ({status.LearningModeProgress:F1}%)");
```

## Baseline Learning Algorithm

The system builds a 168-hour baseline table (7 days × 24 hours) to learn typical speeds for each hour of the week:

1. **Collection**: Speedtest results are collected and grouped by day-of-week and hour
2. **Statistics**: For each hour, calculate mean, stddev, min, max, and median
3. **Blending**: When processing new speedtests:
   - If measured speed is within 10% of baseline: 60/40 blend (favor baseline)
   - If measured speed is >10% below baseline: 80/20 blend (heavily favor baseline)
4. **Overhead**: Apply 5-15% overhead multiplier to account for protocol overhead
5. **Caps**: Apply minimum floor and maximum ceiling

## Latency-Based Adjustment

The ping monitor continuously adjusts bandwidth based on latency:

### High Latency (exceeds baseline + threshold)
- Calculate deviation count: `(latency - baseline) / threshold`
- Apply exponential decrease: `rate × 0.97^deviations`
- Minimum rate: 180 Mbps

### Reduced Latency (below baseline - 0.4ms)
- If rate < 92% of max: Apply 2× increase (`1.04²`)
- If rate < 94% of max: Normalize to 94%
- Otherwise: Keep current rate

### Normal Latency (within 0.3ms of baseline)
- If rate < 90% of max: Apply 4% increase
- If rate < 92% of max: Normalize to 92%
- Otherwise: Keep current rate

## Shell Script Generation

The ScriptGenerator creates deployment-ready scripts:

### Generated Files

1. **20-sqm-speedtest-setup.sh** - Boot script for speedtest setup
   - Installs Ookla speedtest CLI
   - Sets up cron jobs for scheduled speedtests
   - Configures initial baseline run

2. **21-sqm-ping-setup.sh** - Boot script for ping monitoring
   - Sets up cron job for periodic latency checks
   - Excludes speedtest times to avoid conflicts

3. **sqm-speedtest-adjust.sh** - Speedtest execution and rate adjustment
   - Runs speedtest on specified interface
   - Blends with baseline
   - Updates TC classes

4. **sqm-ping-adjust.sh** - Latency monitoring and adjustment
   - Pings target host
   - Calculates rate adjustment
   - Updates TC classes

5. **install.sh** - Deployment script
   - Copies all scripts to appropriate locations
   - Sets permissions
   - Runs initial setup

6. **sqm-metrics-collector.sh** (optional) - InfluxDB metrics
   - Collects current rate, latency, speedtest results
   - Sends to InfluxDB for monitoring

## InfluxDB Integration

Enable metrics collection:

```csharp
config.InfluxDbEndpoint = "https://influxdb.example.com";
config.InfluxDbToken = "your-token";
config.InfluxDbOrg = "your-org";
config.InfluxDbBucket = "sqm-metrics";
```

Metrics collected:
- `current_rate`: Current TC rate in Mbps
- `latency`: Current ping latency in ms
- `speedtest_speed`: Last speedtest result in Mbps

## Configuration Validation

```csharp
var errors = manager.ValidateConfiguration();
if (errors.Any())
{
    foreach (var error in errors)
    {
        Console.WriteLine($"Error: {error}");
    }
}
```

## Deployment to UniFi Device

1. Generate scripts:
   ```csharp
   manager.GenerateScriptsToDirectory("./sqm-scripts");
   ```

2. Copy to device:
   ```bash
   scp -r ./sqm-scripts/* root@192.168.1.1:/tmp/
   ```

3. Install on device:
   ```bash
   ssh root@192.168.1.1
   cd /tmp
   chmod +x install.sh
   ./install.sh
   ```

4. Monitor logs:
   ```bash
   tail -f /var/log/sqm-speedtest-adjust.log
   tail -f /var/log/sqm-ping-adjust.log
   ```

## Advanced Features

### Custom Baseline Import/Export

```csharp
// Export baseline for backup
var baseline = manager.ExportBaselineForScript();
File.WriteAllText("baseline.json", JsonSerializer.Serialize(baseline));

// Import baseline from file
var baselineData = JsonSerializer.Deserialize<Dictionary<string, string>>(
    File.ReadAllText("baseline.json")
);
var calculator = new BaselineCalculator();
calculator.ImportFromShellFormat(baselineData);
```

### Manual Rate Bounds

```csharp
var (minRate, optimalRate, maxRate) = manager.GetRateBounds();
Console.WriteLine($"Min: {minRate}, Optimal: {optimalRate}, Max: {maxRate}");
```

## Architecture

```
SqmManager (Orchestrator)
├── BaselineCalculator (168-hour learning)
├── SpeedtestIntegration (Ookla CLI parser)
├── LatencyMonitor (Ping-based adjustment)
└── ScriptGenerator (Shell script templates)
```

## Dependencies

- .NET 8.0
- System.Text.Json (for speedtest JSON parsing)

## Device Requirements

- UniFi Cloud Gateway or Dream Machine
- Debian-based OS (for apt-get)
- `tc` (traffic control) command
- `jq` (JSON parsing in shell)
- `bc` (bash calculator)
- Internet access for Ookla speedtest CLI installation

## License

Production-ready code for UniFi network optimization.
