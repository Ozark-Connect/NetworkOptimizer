# NetworkOptimizer.Sqm

Adaptive SQM library for UniFi gateways. It generates the self-contained boot scripts that keep a
WAN's Smart Queues shaper just below what the line can deliver, from a weekly congestion schedule,
twice-daily speed test calibration, and a once-a-minute latency loop. It also learns that schedule
from measurements on the line.

See [ARCHITECTURE.md](ARCHITECTURE.md) for the full design: the deploy sequence, every formula in
the generated scripts, the learning pipeline, and the gateway file layout.

## Features

- **Self-contained boot scripts** - One script per WAN in `/data/on_boot.d/` rebuilds everything at
  every boot, so Adaptive SQM survives firmware upgrades.
- **Connection profiles** - Tuned envelopes and latency parameters for GPON, XGS-PON, DOCSIS Cable,
  DSL, Starlink, Fixed Wireless, and Cellular.
- **168-hour congestion schedule** - A built-in weekly pattern per connection type, or a curve
  learned on the line, interpolated at quarter-hour steps.
- **Congestion profile learning** - Hourly short speed tests with the shaper lifted, smoothed into a
  7x24 curve that replaces the type's assumption.
- **Dynamic upload shaping** - On Cellular, Starlink, and Fixed Wireless, upload follows the schedule
  at an adjustable strength, never below half of nominal.
- **Latency loop** - A ping every minute cuts the rate under congestion and restores it as latency
  settles.
- **Pinned Ookla CLI** - Calibration uses a checksum-verified Speedtest by Ookla build installed under
  `/data/network-optimizer/bin`, without touching the gateway's own `speedtest` package.
- **No bc** - All script arithmetic runs in awk. bc is not in the UniFi OS firmware base, so every
  upgrade removes it.

## Components

| Class | Purpose |
|-------|---------|
| `SqmConfiguration` | One WAN's configuration; `ApplyProfileSettings()` and `ApplyLearnedProfile()` fill it |
| `ConnectionProfile` | Per-type envelope, tuning, and the built-in weekly pattern; builds the schedules |
| `ScriptGenerator` | Generates the boot script, with the speedtest and ping scripts embedded |
| `SqmShaperLiftScript` | Runs a gateway command with the shaper lifted, restoring it on exit |
| `CongestionProfileLearner` | Learns a 7x24 curve from hourly samples |
| `LearnedCongestionProfile` | The learned curve, and the measured-to-shaper-rate conversion |
| `CongestionProfileInsights` | Describes a learned curve in words |
| `InputSanitizer` | Validates every value that reaches a shell command line |
| `SqmManager` | Configuration validation (the rest of it is not on the deploy path) |

## Connection Types

| Type | Speed range (of nominal) | Baseline latency |
|------|--------------------------|------------------|
| `Gpon` | 90-105% | 5 ms |
| `XgsPon` | 92-105% | 4 ms |
| `DocsisCable` | 65-95% | 18 ms |
| `Dsl` | 85-95% | 20 ms |
| `Starlink` | 35-110% | 25 ms |
| `FixedWireless` | 50-110% | 15 ms |
| `CellularHome` | 40-120% | 35 ms |

The full parameter table (overhead, thresholds, step sizes, blending weights) is in
[ARCHITECTURE.md](ARCHITECTURE.md#connection-type-parameters).

## Usage

```csharp
using NetworkOptimizer.Sqm;
using NetworkOptimizer.Sqm.Models;

var config = new SqmConfiguration
{
    ConnectionType = ConnectionType.DocsisCable,
    ConnectionName = "Primary WAN",
    Interface = "eth4",
    NominalDownloadSpeed = 300,
    NominalUploadSpeed = 35,
    ShapeUpload = true,
    PingHost = "1.1.1.1",
    SpeedtestSchedule = new List<string> { "0 6 * * *", "30 18 * * *" }
};

// Envelope and tuning from the connection type; pass the WAN link speed when known.
config.ApplyProfileSettings(wanLinkSpeedMbps: 1000);

// Optional: shape from a learned curve instead of the type's pattern.
// config.ApplyLearnedProfile(learnedProfile);

var errors = new SqmManager(config).ValidateConfiguration();

var profile = config.GetProfile();
var baseline = profile.GetHourlyBaseline(config.CongestionSeverity);
var uploadBaseline = config.DynamicUpload
    ? profile.GetHourlyUploadBaseline(config.UploadCongestionSeverity)
    : null;

// { "20-sqm-primary-wan.sh": "<boot script>" }
var scripts = new ScriptGenerator(config, initialDelaySeconds: 5)
    .GenerateAllScripts(baseline, uploadBaseline);
```

In the app, `SqmDeploymentService` uploads the script over SFTP to `/data/on_boot.d/`, runs it, and
deploys the SQM Monitor alongside it.

## Generated Script

`20-sqm-{name}.sh` runs in six sections:

```
Section 1: Install Dependencies (pinned Ookla CLI, jq)
Section 2: Create Directories (/data/sqm)
Section 3: Create Speedtest Adjustment Script (heredoc)
Section 4: Create Ping Adjustment Script (heredoc)
Section 5: Configure Crontab (two calibrations + ping every minute)
Section 6: Schedule Initial Calibration (systemd-run)
```

A failed Ookla install does not fail the deploy. The scripts and cron lines still go in, and
calibration refuses to change rates until the binary validates.

## Logs and Files

- `/var/log/sqm-{name}.log` - Boot script, calibration, and ping loop log
- `/data/sqm/{name}-result.txt` - Last calibrated rate, read by the ping loop
- `/data/sqm/probe-{interface}.lock` - Present while a learning sample has the shaper lifted
- `/data/network-optimizer/bin/speedtest` - Managed Ookla CLI

## Device Requirements

- UniFi gateway with Smart Queues enabled on the WAN (it creates the `ifb` device and HTB classes)
- `udm-boot` for `/data/on_boot.d/` support
- SSH access as root, directly or through the On-Site Agent

## Dependencies

- .NET 10.0

## License

Business Source License 1.1. See [LICENSE](../../LICENSE) in the repository root.

© 2026 Ozark Connect
