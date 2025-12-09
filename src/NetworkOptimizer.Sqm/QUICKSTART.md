# Quick Start Guide - NetworkOptimizer.Sqm

## 5-Minute Setup

### Step 1: Create Configuration (30 seconds)

```csharp
using NetworkOptimizer.Sqm;
using NetworkOptimizer.Sqm.Models;

var config = new SqmConfiguration
{
    Interface = "eth2",              // Your WAN interface
    MaxDownloadSpeed = 285,          // Your ISP max speed
    MinDownloadSpeed = 190,          // Minimum acceptable speed
    PingHost = "40.134.217.121",    // ISP or upstream host
    BaselineLatency = 17.9,         // Your normal ping (run: ping -c 20 HOST)
    LatencyThreshold = 2.2          // Acceptable latency increase
};
```

### Step 2: Generate Scripts (10 seconds)

```csharp
var manager = new SqmManager(config);
manager.GenerateScriptsToDirectory("./sqm-scripts");
```

This creates:
- `20-sqm-speedtest-setup.sh`
- `21-sqm-ping-setup.sh`
- `sqm-speedtest-adjust.sh`
- `sqm-ping-adjust.sh`
- `install.sh`

### Step 3: Deploy to Device (2 minutes)

```bash
# Copy scripts to device
scp -r ./sqm-scripts/* root@YOUR_DEVICE_IP:/tmp/sqm/

# Install
ssh root@YOUR_DEVICE_IP
cd /tmp/sqm
chmod +x install.sh
./install.sh
```

### Step 4: Verify Installation (1 minute)

```bash
# Check if cron jobs are set
crontab -l | grep sqm

# Check if scripts exist
ls -la /data/on-boot.d/*sqm*
ls -la /data/scripts/*sqm*

# Manually run speedtest (optional)
/data/sqm-speedtest-adjust.sh

# Check TC configuration
tc class show dev ifbeth2 | grep "class htb"
```

### Step 5: Monitor (ongoing)

```bash
# Watch speedtest logs
tail -f /var/log/sqm-speedtest-adjust.log

# Watch ping adjustment logs
tail -f /var/log/sqm-ping-adjust.log
```

## That's it!

Your system will now:
- Run speedtest at 6 AM and 6:30 PM
- Adjust bandwidth every 5 minutes based on latency
- Maintain optimal speeds automatically

## Common Adjustments

### Change Speedtest Schedule

```csharp
config.SpeedtestSchedule = new List<string>
{
    "0 */4 * * *"  // Every 4 hours
};
```

### Change Ping Interval

```csharp
config.PingAdjustmentInterval = 2;  // Every 2 minutes
```

### More Aggressive Rate Decrease

```csharp
config.LatencyDecrease = 0.95;  // 5% decrease instead of 3%
```

### Faster Recovery

```csharp
config.LatencyIncrease = 1.05;  // 5% increase instead of 4%
```

## Finding Your Baseline Latency

```bash
# On your UniFi device, ping your ISP gateway or upstream provider
ping -I eth2 -c 100 YOUR_ISP_IP

# Look at the "avg" value in the summary:
# rtt min/avg/max/mdev = 16.2/17.9/22.5/1.8 ms
#                              ^^^^ Use this value
```

Set `BaselineLatency = 17.9` and `LatencyThreshold = 2.0` (or adjust to taste).

## Troubleshooting

### Scripts not running
```bash
# Check cron logs
grep CRON /var/log/syslog

# Run manually to see errors
/data/sqm-speedtest-adjust.sh
/data/sqm-ping-adjust.sh
```

### Speedtest not installed
```bash
# The boot script should install it, but you can manually run:
/data/on-boot.d/20-sqm-speedtest-setup.sh
```

### TC classes not updating
```bash
# Check if ifb device exists
tc class show dev ifbeth2

# If not, check SQM is enabled in UniFi UI
# Network > Settings > Internet > WAN > Smart Queue > Enable
```

## Learning Mode (Optional)

For best results, run in learning mode for 7 days:

```csharp
manager.StartLearningMode();
manager.GenerateScriptsToDirectory("./sqm-learning");

// Deploy scripts, let run for 7 days

// After 7 days, check progress
var progress = manager.GetLearningProgress();
if (manager.IsLearningComplete())
{
    manager.StopLearningMode();
    manager.GenerateScriptsToDirectory("./sqm-production");
    // Deploy production scripts with learned baseline
}
```

## Next Steps

- Read [README.md](README.md) for detailed documentation
- Check [Examples/BasicUsage.cs](Examples/BasicUsage.cs) for more examples
- Run [Demo/Program.cs](Demo/Program.cs) to see it in action
- Review [SUMMARY.md](SUMMARY.md) for architecture details

## Support

For issues or questions, check the logs first:
```bash
tail -100 /var/log/sqm-speedtest-adjust.log
tail -100 /var/log/sqm-ping-adjust.log
```

Most issues are related to:
1. Incorrect interface name (check with `ip addr`)
2. SQM not enabled in UniFi UI
3. Missing dependencies (jq, bc, speedtest)
4. Firewall blocking speedtest

The boot scripts handle most dependencies automatically.
