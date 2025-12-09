# NetworkOptimizer.Sqm - Architecture Diagram

## System Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                          SqmManager                                 │
│                      (Main Orchestrator)                            │
│                                                                     │
│  • ConfigureSqm()          • GenerateScripts()                     │
│  • StartLearningMode()     • GetStatus()                           │
│  • TriggerSpeedtest()      • ApplyRateAdjustment()                 │
│  • ValidateConfiguration()  • GetRateBounds()                      │
└──────────┬──────────┬──────────┬──────────┬─────────────────────────┘
           │          │          │          │
           ▼          ▼          ▼          ▼
    ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────────┐
    │Baseline  │ │Speedtest │ │ Latency  │ │    Script    │
    │Calculator│ │Integration│ │ Monitor  │ │  Generator   │
    └──────────┘ └──────────┘ └──────────┘ └──────────────┘
```

## Component Interactions

```
User Code
    │
    │ 1. Configure
    ▼
┌────────────────┐
│  SqmManager    │
└────────┬───────┘
         │
         │ 2. Generate Scripts
         ▼
┌────────────────┐     Baseline Data      ┌──────────────────┐
│ScriptGenerator │◄──────────────────────│BaselineCalculator│
└────────┬───────┘                        └──────────────────┘
         │
         │ 3. Output Shell Scripts
         ▼
┌─────────────────────────────────────────┐
│   Shell Scripts (for UniFi Device)      │
│                                          │
│  • 20-sqm-speedtest-setup.sh           │
│  • 21-sqm-ping-setup.sh                │
│  • sqm-speedtest-adjust.sh             │
│  • sqm-ping-adjust.sh                  │
│  • install.sh                           │
└──────────────────────────────────────────┘
         │
         │ 4. Deploy to Device
         ▼
┌──────────────────────────────────────────┐
│      UniFi Cloud Gateway / UDM           │
│                                          │
│  Cron Jobs:                             │
│  ┌────────────────────────────────────┐ │
│  │ 6:00 AM  → Speedtest              │ │
│  │ 6:30 PM  → Speedtest              │ │
│  │ Every 5m → Ping Adjustment        │ │
│  └────────────────────────────────────┘ │
│                                          │
│  TC Classes:                            │
│  ┌────────────────────────────────────┐ │
│  │ ifbeth2 (ingress shaping)         │ │
│  │ - Root class 1:1                  │ │
│  │ - Child classes (priority queues) │ │
│  └────────────────────────────────────┘ │
│                                          │
│  Logs:                                  │
│  • /var/log/sqm-speedtest-adjust.log   │
│  • /var/log/sqm-ping-adjust.log        │
└──────────────────────────────────────────┘
```

## Data Flow

### Speedtest Flow
```
┌──────────────────────────────────────────────────────────────────┐
│ 1. Scheduled Trigger (Cron)                                     │
└──────────────────────┬───────────────────────────────────────────┘
                       │
                       ▼
┌──────────────────────────────────────────────────────────────────┐
│ 2. sqm-speedtest-adjust.sh                                       │
│    • Set TC to max speed                                         │
│    • Run: speedtest --format=json --interface=eth2             │
└──────────────────────┬───────────────────────────────────────────┘
                       │
                       ▼
┌──────────────────────────────────────────────────────────────────┐
│ 3. Parse JSON Output                                            │
│    download.bandwidth (bytes/sec) → Mbps                        │
└──────────────────────┬───────────────────────────────────────────┘
                       │
                       ▼
┌──────────────────────────────────────────────────────────────────┐
│ 4. Apply Minimum Floor                                          │
│    measured_speed = max(measured_speed, MIN_SPEED)              │
└──────────────────────┬───────────────────────────────────────────┘
                       │
                       ▼
┌──────────────────────────────────────────────────────────────────┐
│ 5. Look Up Baseline                                             │
│    BASELINE[day_hour] → baseline_speed                          │
└──────────────────────┬───────────────────────────────────────────┘
                       │
                       ▼
┌──────────────────────────────────────────────────────────────────┐
│ 6. Apply Blending                                               │
│    if measured >= baseline * 0.9:                               │
│        blended = baseline * 0.6 + measured * 0.4   (60/40)     │
│    else:                                                         │
│        blended = baseline * 0.8 + measured * 0.2   (80/20)     │
└──────────────────────┬───────────────────────────────────────────┘
                       │
                       ▼
┌──────────────────────────────────────────────────────────────────┐
│ 7. Apply Overhead                                               │
│    effective = blended * OVERHEAD_MULTIPLIER                    │
└──────────────────────┬───────────────────────────────────────────┘
                       │
                       ▼
┌──────────────────────────────────────────────────────────────────┐
│ 8. Apply Caps                                                   │
│    • Cap at MAX_DOWNLOAD_SPEED                                  │
│    • Cap at 95% of MAX_DOWNLOAD_SPEED                           │
└──────────────────────┬───────────────────────────────────────────┘
                       │
                       ▼
┌──────────────────────────────────────────────────────────────────┐
│ 9. Update TC Classes                                            │
│    update_all_tc_classes(ifbeth2, effective_speed)             │
└──────────────────────┬───────────────────────────────────────────┘
                       │
                       ▼
┌──────────────────────────────────────────────────────────────────┐
│ 10. Save Result & Log                                           │
│     echo "Measured download speed: $speed Mbps" > result.txt   │
│     Log to /var/log/sqm-speedtest-adjust.log                   │
└──────────────────────────────────────────────────────────────────┘
```

### Ping Adjustment Flow
```
┌──────────────────────────────────────────────────────────────────┐
│ 1. Periodic Trigger (Every 5 minutes)                           │
└──────────────────────┬───────────────────────────────────────────┘
                       │
                       ▼
┌──────────────────────────────────────────────────────────────────┐
│ 2. sqm-ping-adjust.sh                                           │
│    • Read last speedtest result                                 │
│    • Look up current baseline                                   │
└──────────────────────┬───────────────────────────────────────────┘
                       │
                       ▼
┌──────────────────────────────────────────────────────────────────┐
│ 3. Calculate MAX_DOWNLOAD_SPEED                                 │
│    • Baseline + overhead                                        │
│    • Blend with speedtest result (60/40)                        │
└──────────────────────┬───────────────────────────────────────────┘
                       │
                       ▼
┌──────────────────────────────────────────────────────────────────┐
│ 4. Measure Latency                                              │
│    ping -I eth2 -c 20 -i 0.25 -q HOST → avg latency            │
└──────────────────────┬───────────────────────────────────────────┘
                       │
                       ▼
┌──────────────────────────────────────────────────────────────────┐
│ 5. Calculate Deviation                                          │
│    deviation_count = (latency - baseline) / threshold           │
└──────────────────────┬───────────────────────────────────────────┘
                       │
                       ▼
┌──────────────────────────────────────────────────────────────────┐
│ 6. Determine Adjustment                                         │
│                                                                  │
│    IF latency >= baseline + threshold:                          │
│        new_rate = current * (0.97^deviation_count)              │
│        (Exponential decrease)                                   │
│                                                                  │
│    ELIF latency < baseline - 0.4:                               │
│        (Reduced latency - increase rate)                        │
│        IF current < 92% max: new_rate = current * 1.04²         │
│        ELIF current < 94% max: new_rate = 94% max               │
│        ELSE: new_rate = current                                 │
│                                                                  │
│    ELSE (normal latency):                                       │
│        IF current < 90% max AND latency_diff <= 0.3:            │
│            new_rate = current * 1.04                            │
│        ELIF current < 92% max AND latency_diff <= 0.3:          │
│            new_rate = 92% max                                   │
│        ELSE: new_rate = current                                 │
└──────────────────────┬───────────────────────────────────────────┘
                       │
                       ▼
┌──────────────────────────────────────────────────────────────────┐
│ 7. Apply Safety Caps                                           │
│    • Cap at 95% of ABSOLUTE_MAX                                 │
│    • Round to 1 decimal place                                   │
└──────────────────────┬───────────────────────────────────────────┘
                       │
                       ▼
┌──────────────────────────────────────────────────────────────────┐
│ 8. Update TC Classes                                            │
│    update_all_tc_classes(ifbeth2, new_rate)                    │
└──────────────────────┬───────────────────────────────────────────┘
                       │
                       ▼
┌──────────────────────────────────────────────────────────────────┐
│ 9. Log Adjustment                                               │
│    Log to /var/log/sqm-ping-adjust.log                         │
└──────────────────────────────────────────────────────────────────┘
```

## Class Hierarchy

```
Models
│
├── SqmConfiguration
│   ├── Interface settings
│   ├── Speed limits
│   ├── Latency thresholds
│   ├── Scheduling
│   ├── Learning mode
│   └── InfluxDB (optional)
│
├── SqmStatus
│   ├── Current rate
│   ├── Last speedtest
│   ├── Current latency
│   ├── Baseline speed
│   ├── Learning progress
│   └── Last adjustment
│
├── BaselineData
│   ├── HourlyBaseline
│   │   ├── Day/Hour
│   │   ├── Statistics (mean, stddev, min, max, median)
│   │   └── Sample count
│   │
│   ├── BaselineTable
│   │   ├── 168 HourlyBaseline entries
│   │   ├── Collection timestamps
│   │   ├── Completeness flag
│   │   └── Lookup methods
│   │
│   └── SpeedtestSample
│       ├── Timestamp
│       ├── Day/Hour
│       └── Speeds (download, upload, latency)
│
└── SpeedtestResult
    ├── Timestamp
    ├── Ping info
    ├── Download info (bandwidth, bytes, latency)
    ├── Upload info
    ├── Interface info
    └── Server info
```

## Component Dependencies

```
SqmManager
    │
    ├──► BaselineCalculator
    │       │
    │       └──► BaselineTable
    │               └──► HourlyBaseline (×168)
    │
    ├──► SpeedtestIntegration
    │       │
    │       ├──► SpeedtestResult (JSON model)
    │       └──► SpeedtestSample (for baseline)
    │
    ├──► LatencyMonitor
    │       └──► SqmConfiguration
    │
    └──► ScriptGenerator
            └──► BaselineTable → Shell Format
```

## State Machine

```
                    ┌──────────────┐
                    │ Unconfigured │
                    └──────┬───────┘
                           │ ConfigureSqm()
                           ▼
                    ┌──────────────┐
              ┌────►│  Configured  │◄────┐
              │     └──────┬───────┘     │
              │            │              │
              │            │ StartLearningMode()
              │            ▼              │
              │     ┌──────────────┐     │
              │     │   Learning   │     │
              │     │   (0-100%)   │     │
              │     └──────┬───────┘     │
              │            │              │
              │            │ Complete     │
              │            ▼              │
              │     ┌──────────────┐     │
              │     │  Production  │     │
              │     │  (Operating) │─────┘
              │     └──────┬───────┘
              │            │
              │            │ GenerateScripts()
              │            ▼
              │     ┌──────────────┐
              │     │   Deployed   │
              │     └──────┬───────┘
              │            │
              │            │ Runtime
              │            ▼
              │     ┌──────────────┐
              └─────┤   Adjusting  │
                    │ (Continuous) │
                    └──────────────┘
```

## Timeline (Typical Deployment)

```
Day 0 (Initial Setup)
├── Generate scripts with initial configuration
├── Deploy to device
├── First speedtest at 6 AM (next day)
└── Ping adjustments begin (every 5 minutes)

Day 1-7 (Learning Phase)
├── Speedtest at 6 AM and 6:30 PM daily
├── Collect baseline data for each hour
├── Ping adjustments continue
└── Baseline completeness: 0% → 100%

Day 7+ (Production Phase)
├── Full baseline established
├── Speedtest blending with 60/40 or 80/20
├── Optimal rate stability
└── Automatic adjustments based on latency

Ongoing (Maintenance)
├── Baseline slowly evolves (incremental updates)
├── Monitor logs for anomalies
├── Adjust thresholds if needed
└── Review metrics in InfluxDB (if enabled)
```

## Error Handling Flow

```
┌─────────────────────┐
│ Script Execution    │
└──────────┬──────────┘
           │
           ▼
    ┌─────────────┐
    │  Pre-checks │
    │  • jq       │
    │  • bc       │
    │  • speedtest│
    └──────┬──────┘
           │
           │ ✓ OK
           ▼
    ┌─────────────┐
    │   Execute   │
    └──────┬──────┘
           │
           ├──► Error → Log to file → Continue
           │
           └──► Success → Update TC → Log result
```

## Monitoring Points

```
┌────────────────────────────────────────────────────────────┐
│                    Monitoring Layer                        │
├────────────────────────────────────────────────────────────┤
│                                                            │
│  Logs:                                                     │
│  ├── /var/log/sqm-speedtest-adjust.log                   │
│  │   • Speedtest results                                  │
│  │   • Rate calculations                                  │
│  │   • TC updates                                         │
│  │                                                         │
│  └── /var/log/sqm-ping-adjust.log                        │
│      • Latency measurements                               │
│      • Rate adjustments                                   │
│      • Decision reasoning                                 │
│                                                            │
│  InfluxDB (optional):                                     │
│  └── sqm measurement                                      │
│      ├── current_rate (field)                            │
│      ├── latency (field)                                 │
│      ├── speedtest_speed (field)                         │
│      └── interface (tag)                                 │
│                                                            │
│  TC Classes:                                              │
│  └── tc class show dev ifbeth2                           │
│      • Real-time rate verification                       │
│      • Queue status                                       │
└────────────────────────────────────────────────────────────┘
```

## Performance Optimization

```
Optimization Strategy
│
├── Caching
│   └── Baseline table in memory (associative array)
│
├── Lazy Evaluation
│   └── Only calculate when needed
│
├── Incremental Updates
│   └── Update single baseline entries (not full recalc)
│
└── Efficient Queries
    └── O(1) baseline lookup by day_hour key
```

This architecture provides a robust, production-ready SQM system with clear separation of concerns, comprehensive error handling, and extensive monitoring capabilities.
