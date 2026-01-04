# NetworkOptimizer.Sqm

Smart Queue Management (SQM) library for UniFi gateways (UCG/UDM). Generates self-contained boot scripts that implement adaptive bandwidth management with baseline learning and latency-based rate adjustment.

## Features

- **Self-contained boot scripts** - Single script survives firmware upgrades via `/data/on_boot.d/`
- **Connection profiles** - Pre-tuned settings for DOCSIS Cable, Starlink, Fiber, DSL, Fixed Wireless, and Cellular
- **168-hour baseline patterns** - Built-in hourly speed patterns based on real-world connection data
- **Latency-based adjustment** - Ping monitoring with automatic rate decrease/increase
- **Speedtest integration** - Ookla CLI with baseline blending

## Components

| Class | Purpose |
|-------|---------|
| `SqmManager` | Main orchestrator for SQM operations |
| `SqmConfiguration` | Configuration model with profile-based defaults |
| `ConnectionProfile` | Connection type with calculated speed/latency parameters |
| `ScriptGenerator` | Generates self-contained boot script |
| `BaselineCalculator` | 168-hour baseline learning and statistics |
| `SpeedtestIntegration` | Ookla speedtest JSON parsing |
| `LatencyMonitor` | Ping-based rate adjustment calculations |

## Connection Types

Each connection type has tuned parameters for speed ranges, latency thresholds, and blending ratios:

| Type | Description | Speed Range | Latency |
|------|-------------|-------------|---------|
| `DocsisCable` | DOCSIS Cable (Coax) | 65-95% of nominal | 18ms baseline |
| `Starlink` | Satellite | 35-110% of nominal | 25ms baseline |
| `Fiber` | FTTH/FTTP | 90-105% of nominal | 5ms baseline |
| `Dsl` | ADSL/VDSL | 85-95% of nominal | 20ms baseline |
| `FixedWireless` | WISP | 50-110% of nominal | 15ms baseline |
| `CellularHome` | Fixed LTE/5G | 40-120% of nominal | 35ms baseline |

## Usage

### Create Configuration from Profile

```csharp
using NetworkOptimizer.Sqm;
using NetworkOptimizer.Sqm.Models;

var config = new SqmConfiguration
{
    ConnectionType = ConnectionType.DocsisCable,
    ConnectionName = "Primary WAN",
    Interface = "eth2",
    NominalDownloadSpeed = 300,
    NominalUploadSpeed = 35,
    PingHost = "1.1.1.1"
};

// Apply calculated parameters from connection profile
config.ApplyProfileSettings();

Console.WriteLine(config.GetParameterSummary());
// Output:
// Connection: DOCSIS Cable (Primary WAN)
// Interface: eth2 (IFB: ifbeth2)
// Nominal Speed: 300/35 Mbps (down/up)
// Speed Range: 195-285 Mbps (floor-ceiling)
// ...
```

### Generate Boot Script

```csharp
var manager = new SqmManager(config);

// Get baseline from connection profile
var profile = config.GetProfile();
var baseline = profile.GetHourlyBaseline();

// Generate and save scripts
manager.GenerateScriptsToDirectory("/output/path");
// Creates: 20-sqm-primary-wan.sh
```

### Process Speedtest Results

```csharp
string speedtestJson = File.ReadAllText("speedtest-result.json");
var effectiveRate = await manager.TriggerSpeedtest(speedtestJson);
Console.WriteLine($"Effective rate: {effectiveRate} Mbps");
```

### Apply Latency-Based Adjustment

```csharp
double currentLatency = 22.5; // ms
double currentRate = 265; // Mbps

var (adjustedRate, reason) = manager.ApplyRateAdjustment(currentLatency, currentRate);
Console.WriteLine($"Adjusted to {adjustedRate} Mbps: {reason}");
```

## Generated Script

The `ScriptGenerator` creates a single self-contained boot script (`20-sqm-{name}.sh`) that:

1. **Installs dependencies** - Ookla speedtest, bc, jq via apt-get
2. **Creates /data/sqm/ directory** - Persistent storage for result files
3. **Embeds scripts via heredoc** - Speedtest and ping adjustment scripts
4. **Configures crontab** - Scheduled speedtests and ping adjustments
5. **Schedules initial calibration** - First speedtest runs shortly after boot

### Script Sections

```
Section 1: Install Dependencies (speedtest, bc, jq)
Section 2: Create Directories (/data/sqm)
Section 3: Create Speedtest Script (embedded via heredoc)
Section 4: Create Ping Script (embedded via heredoc)
Section 5: Configure Crontab (speedtest schedule + ping interval)
Section 6: Schedule Initial Calibration (via systemd-run)
```

## Baseline Blending

When processing speedtest results, measured speed is blended with historical baseline:

| Condition | DOCSIS | Starlink | Fiber |
|-----------|--------|----------|-------|
| Within 10% of baseline | 60/40 (baseline/measured) | 50/50 | 70/30 |
| Below 10% of baseline | 80/20 | 70/30 | 85/15 |

This prevents temporary dips from over-correcting the rate.

## Latency Adjustment Algorithm

The ping script adjusts rates based on measured latency vs baseline:

**High Latency** (exceeds baseline + threshold):
- Calculate deviation count: `(latency - baseline) / threshold`
- Apply exponential decrease: `rate × 0.97^deviations`
- Minimum: floor speed from profile

**Low Latency** (below baseline - 0.4ms):
- If rate < 92% of max: Apply double increase
- If rate < 94% of max: Normalize to 94%
- Otherwise: maintain current rate

**Normal Latency** (within baseline ± threshold):
- Gradual increase toward optimal rate

## Deployment

1. Generate script via `SqmManager.GenerateScriptsToDirectory()`
2. Copy to UniFi gateway: `scp 20-sqm-*.sh root@gateway:/data/on_boot.d/`
3. Make executable: `chmod +x /data/on_boot.d/20-sqm-*.sh`
4. Run manually or reboot to activate

The script will:
- Install Ookla speedtest CLI (removes UniFi's incompatible version)
- Set up cron jobs for scheduled speedtests
- Run initial calibration ~60 seconds after boot
- Adjust TC classes on the IFB device

## Logs

- `/var/log/sqm-{name}.log` - Boot script and adjustment logs
- `/data/sqm/{name}-result.txt` - Last speedtest result for ping script

## Dependencies

- .NET 10.0

## Device Requirements

- UniFi Cloud Gateway or Dream Machine
- SSH access with root
- `udm-boot` package (for /data/on_boot.d/ support)

## License

Business Source License 1.1. See [LICENSE](../../LICENSE) in the repository root.

© 2026 Ozark Connect
