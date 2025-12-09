# Network Optimizer Agent

Standalone agent scripts for network monitoring and optimization on UniFi Dream Machine/Cloud Gateway and Linux systems.

## Overview

This project provides intelligent network monitoring and optimization agents that:

- **Monitor network performance** via latency testing and speedtest measurements
- **Dynamically adjust bandwidth** using traffic control (TC) and HTB queuing
- **Collect system metrics** (CPU, memory, disk, network, Docker stats)
- **Push metrics to InfluxDB** for visualization and analysis
- **Run autonomously** with minimal configuration

## Architecture

```
agents/
├── udm/                    # UniFi Dream Machine / Cloud Gateway agents
│   ├── 50-network-optimizer-agent.sh    # On-boot setup script
│   ├── sqm-manager.sh                   # Speedtest-based SQM management
│   ├── sqm-ping-monitor.sh              # Latency-based bandwidth adjustment
│   ├── metrics-collector.sh             # Metrics collection and InfluxDB push
│   └── install.sh                       # Installation script
│
└── linux/                  # Standard Linux system agents
    ├── network-optimizer-agent.sh       # System metrics collector
    ├── network-optimizer-agent.service  # Systemd unit file
    └── install.sh                       # Installation script
```

---

## UDM/UCG Agents

### Components

#### 1. `50-network-optimizer-agent.sh` - On-Boot Setup Script

**Purpose**: Self-contained bootstrap script that runs on gateway boot.

**Location**: `/data/on_boot.d/50-network-optimizer-agent.sh`

**Features**:
- Installs dependencies (speedtest, bc, jq)
- Generates agent scripts if missing
- Sets up cron jobs
- Starts background processes
- Idempotent (safe to run multiple times)

**Configuration**:
```bash
INFLUXDB_URL="http://your-influxdb-host:8086"
INFLUXDB_TOKEN="your-token-here"
INFLUXDB_ORG="your-org"
INFLUXDB_BUCKET="network-metrics"
```

---

#### 2. `sqm-manager.sh` - SQM Speedtest Management

**Purpose**: Performs speedtest and adjusts traffic control bandwidth limits.

**Schedule**: Runs at 6:00 AM and 6:30 PM daily (via cron)

**Algorithm**:
1. Run Ookla speedtest on WAN interface
2. Look up 168-hour baseline for current day/hour
3. Blend measured and baseline speeds:
   - **60/40 blend** (baseline/measured) if within 10% of baseline
   - **80/20 blend** (baseline/measured) if below 10% threshold
4. Apply overhead multiplier (default: 1.05 = 5%)
5. Update HTB tc classes on IFB device

**Key Variables**:
```bash
WAN_INTERFACE="eth2"              # Physical WAN interface
IFB_INTERFACE="ifbeth2"           # IFB shaping interface
MAX_DOWNLOAD_SPEED="285"          # Max speed in Mbps
MIN_DOWNLOAD_SPEED="190"          # Min speed in Mbps
DOWNLOAD_SPEED_MULTIPLIER="1.05"  # 5% overhead
BASELINE_THRESHOLD_PCT="0.90"     # 90% threshold for blending
```

**Baseline Table**:
- 168-hour lookup table (7 days × 24 hours)
- Indexed by day (0=Mon, 6=Sun) and hour (0-23)
- Customize based on ISP performance patterns

---

#### 3. `sqm-ping-monitor.sh` - Latency-Based Adjustment

**Purpose**: Monitors latency and dynamically adjusts bandwidth to maintain low latency.

**Schedule**: Runs every 5 minutes (except during speedtest windows)

**Algorithm**:
1. Ping upstream target (20 packets, 0.25s interval)
2. Calculate latency deviation from baseline
3. Adjust bandwidth:
   - **High latency**: Apply `0.97^n` reduction (3% per deviation unit)
   - **Low latency**: Apply `1.04^n` increase (4% recovery)
   - **Normal latency**: Maintain or normalize to optimal range
4. Update HTB tc classes

**Key Variables**:
```bash
ISP_PING_HOST="40.134.217.121"    # Upstream ping target
BASELINE_LATENCY="17.9"           # Optimal ping in ms
LATENCY_THRESHOLD="2.2"           # Deviation tolerance in ms
LATENCY_DECREASE="0.97"           # 3% decrease multiplier
LATENCY_INCREASE="1.04"           # 4% increase multiplier
ABSOLUTE_MAX_DOWNLOAD_SPEED="280" # Absolute ceiling in Mbps
MIN_DOWNLOAD_SPEED="180"          # Absolute floor in Mbps
```

**Ping Target Options**:
- **ISP Host**: Most sensitive, closest to your network
- **Upstream Provider** (recommended): Balance of sensitivity and stability
- **Stable Internet Host** (e.g., 1.1.1.1): Less sensitive to local issues

---

#### 4. `metrics-collector.sh` - Metrics Collection

**Purpose**: Collects system and network metrics, sends to InfluxDB.

**Schedule**: Runs every minute (via cron)

**Metrics Collected**:
- **TC Stats**: Rate limits, bytes sent, packets sent
- **System Stats**: CPU usage, memory usage, disk usage, load average, uptime
- **Network Stats**: RX/TX bytes, packets, errors for WAN interface

**Key Variables**:
```bash
INFLUXDB_URL="http://your-influxdb-host:8086"
INFLUXDB_TOKEN="your-token-here"
INFLUXDB_ORG="your-org"
INFLUXDB_BUCKET="network-metrics"
COLLECT_TC_STATS="true"
COLLECT_SYSTEM_STATS="true"
COLLECT_NETWORK_STATS="true"
DRY_RUN="false"  # Set to "true" to test without sending
```

**InfluxDB Line Protocol Format**:
```
tc_rate,host=gateway,interface=ifbeth2,class=root value=256 1638360000000000000
system_cpu_usage,host=gateway value=45.2 1638360000000000000
network_rx_bytes,host=gateway,interface=eth2 value=1234567890 1638360000000000000
```

---

#### 5. `install.sh` - Installation Script

**Purpose**: Automates installation of all UDM/UCG agent components.

**Usage**:
```bash
# On your computer, copy files to gateway
scp -r agents/udm/* root@gateway-ip:/tmp/

# SSH to gateway
ssh root@gateway-ip

# Run installer
cd /tmp
chmod +x install.sh
./install.sh
```

**What It Does**:
1. Checks platform (must be UniFi gateway)
2. Creates directories (`/data/network-optimizer-agent`, `/data/on_boot.d`)
3. Installs dependencies (speedtest, bc, jq, curl)
4. Copies scripts to correct locations
5. Sets executable permissions
6. Installs cron jobs
7. Runs initial calibration
8. Verifies installation

---

### Installation

#### Quick Start

```bash
# 1. Clone or download this repository
git clone https://github.com/yourusername/network-optimizer-agent.git
cd network-optimizer-agent/agents/udm

# 2. Edit configuration in scripts (IMPORTANT!)
nano sqm-manager.sh          # Set WAN_INTERFACE, MAX_DOWNLOAD_SPEED, etc.
nano sqm-ping-monitor.sh     # Set ISP_PING_HOST, BASELINE_LATENCY, etc.
nano metrics-collector.sh    # Set INFLUXDB_URL, INFLUXDB_TOKEN, etc.

# 3. Copy to gateway
scp *.sh root@gateway-ip:/tmp/

# 4. SSH to gateway and install
ssh root@gateway-ip
cd /tmp
chmod +x install.sh
./install.sh

# 5. Verify installation
tail -f /var/log/network-optimizer-agent-setup.log
crontab -l
ls -la /data/network-optimizer-agent/
```

#### Manual Installation

```bash
# 1. Create directories
mkdir -p /data/network-optimizer-agent
mkdir -p /data/on_boot.d

# 2. Copy scripts
cp 50-network-optimizer-agent.sh /data/on_boot.d/
cp sqm-manager.sh sqm-ping-monitor.sh metrics-collector.sh /data/network-optimizer-agent/

# 3. Set permissions
chmod +x /data/on_boot.d/50-network-optimizer-agent.sh
chmod +x /data/network-optimizer-agent/*.sh

# 4. Install dependencies
apt-get update
apt-get install -y bc jq curl

# Remove UniFi's speedtest
apt-get remove -y speedtest

# Install Ookla speedtest
curl -s https://packagecloud.io/install/repositories/ookla/speedtest-cli/script.deb.sh | bash
apt-get install -y speedtest

# 5. Run on-boot script once to set up cron jobs
/data/on_boot.d/50-network-optimizer-agent.sh
```

---

### Configuration

#### Customize Baseline Data

Edit `sqm-manager.sh` and `sqm-ping-monitor.sh` to update the 168-hour baseline:

```bash
# Collect speedtest data for a week, then calculate median per hour
# Example for Monday 6 AM:
BASELINE[0_6]="255"  # 255 Mbps median speed
```

#### Tune Ping Monitor

Determine your optimal baseline latency:

```bash
# Run unloaded ping test
ping -I eth2 -c 100 -i 0.25 your-upstream-host

# Take the average latency and set:
BASELINE_LATENCY="17.9"  # Your measured average
LATENCY_THRESHOLD="2.2"  # ~12% of baseline as threshold
```

#### Configure InfluxDB

Edit `metrics-collector.sh`:

```bash
INFLUXDB_URL="http://192.168.1.100:8086"
INFLUXDB_TOKEN="your-influxdb-api-token"
INFLUXDB_ORG="home"
INFLUXDB_BUCKET="network-metrics"
```

---

### Monitoring

#### Check Logs

```bash
# Setup log
tail -f /var/log/network-optimizer-agent-setup.log

# SQM manager log
tail -f /var/log/sqm-manager.log

# Ping monitor log
tail -f /var/log/sqm-ping-monitor.log

# Metrics collector log
tail -f /var/log/metrics-collector.log
```

#### Verify Cron Jobs

```bash
# List cron jobs
crontab -l

# Should show:
# 0 6 * * * ... sqm-manager.sh
# 30 18 * * * ... sqm-manager.sh
# */5 * * * * ... sqm-ping-monitor.sh
# * * * * * ... metrics-collector.sh
```

#### Check TC Configuration

```bash
# View current TC classes
tc class show dev ifbeth2 | grep "class htb"

# Should show current rate and ceil values
# Example: class htb 1:1 root rate 256Mbit ceil 256Mbit
```

#### Manual Test Runs

```bash
# Test SQM manager
/data/network-optimizer-agent/sqm-manager.sh

# Test ping monitor
/data/network-optimizer-agent/sqm-ping-monitor.sh

# Test metrics collector (dry run)
DRY_RUN=true /data/network-optimizer-agent/metrics-collector.sh
```

---

### Troubleshooting

#### Speedtest Fails

```bash
# Test speedtest manually
speedtest --accept-license --format=json --interface=eth2

# Reinstall if needed
apt-get remove -y speedtest
curl -s https://packagecloud.io/install/repositories/ookla/speedtest-cli/script.deb.sh | bash
apt-get install -y speedtest
```

#### TC Commands Fail

```bash
# Check if SQM is enabled in UniFi UI
# Network > Settings > Internet > WAN > Smart Queue Management

# Verify IFB device exists
ip link show ifbeth2

# Check current TC configuration
tc qdisc show dev ifbeth2
tc class show dev ifbeth2
```

#### Metrics Not Appearing in InfluxDB

```bash
# Test metrics collector in dry run mode
DRY_RUN=true DEBUG=true /data/network-optimizer-agent/metrics-collector.sh

# Test InfluxDB connection
curl -i -X POST \
  "http://your-influxdb:8086/api/v2/write?org=your-org&bucket=network-metrics&precision=ns" \
  -H "Authorization: Token your-token" \
  -H "Content-Type: text/plain" \
  --data-binary "test_metric,host=gateway value=1 $(date +%s)000000000"

# Check InfluxDB logs
# (on InfluxDB server)
docker logs influxdb
```

---

## Linux Agents

### Components

#### 1. `network-optimizer-agent.sh` - System Metrics Collector

**Purpose**: Collects system metrics and sends to InfluxDB.

**Features**:
- Runs as systemd service
- Collects CPU, memory, disk, load, network stats
- Optionally collects Docker container stats
- Configurable via environment variables

**Key Variables**:
```bash
INFLUXDB_URL="http://your-influxdb-host:8086"
INFLUXDB_TOKEN="your-token-here"
INFLUXDB_ORG="your-org"
INFLUXDB_BUCKET="network-metrics"
COLLECTION_INTERVAL="60"          # Seconds between collections
COLLECT_SYSTEM_STATS="true"
COLLECT_DOCKER_STATS="false"      # Set to "true" to enable
COLLECT_NETWORK_STATS="true"
NETWORK_INTERFACES="eth0"         # Comma-separated list
```

**Metrics Collected**:
- **System**: CPU usage, memory (total/used/free/available), disk usage, load average, uptime, process count
- **Network**: RX/TX bytes, packets, errors, dropped packets per interface
- **Docker** (optional): Container CPU, memory, network I/O, block I/O

---

#### 2. `network-optimizer-agent.service` - Systemd Unit File

**Purpose**: Systemd service definition for the agent.

**Features**:
- Auto-restart on failure
- Runs as root (required for full system metrics)
- Logs to `/var/log/network-optimizer-agent.log`
- Security hardening (ProtectSystem, PrivateTmp)

**Configuration**: Edit environment variables in the service file:

```ini
Environment="INFLUXDB_URL=http://192.168.1.100:8086"
Environment="INFLUXDB_TOKEN=your-token-here"
Environment="INFLUXDB_ORG=home"
Environment="INFLUXDB_BUCKET=network-metrics"
Environment="COLLECTION_INTERVAL=60"
Environment="COLLECT_DOCKER_STATS=true"
Environment="NETWORK_INTERFACES=eth0,wlan0"
```

---

#### 3. `install.sh` - Installation Script

**Purpose**: Automates installation on Linux systems.

**Usage**:
```bash
sudo ./install.sh
```

**What It Does**:
1. Checks for root privileges
2. Detects Linux distribution
3. Creates installation directory (`/opt/network-optimizer-agent`)
4. Installs dependencies (bc, curl)
5. Copies agent script
6. Installs systemd service
7. Enables and starts service
8. Verifies installation

---

### Installation

#### Quick Start

```bash
# 1. Clone or download this repository
git clone https://github.com/yourusername/network-optimizer-agent.git
cd network-optimizer-agent/agents/linux

# 2. Edit systemd service file to configure
nano network-optimizer-agent.service
# Update Environment variables (INFLUXDB_URL, INFLUXDB_TOKEN, etc.)

# 3. Run installer
chmod +x install.sh
sudo ./install.sh

# 4. Reload systemd and restart service
sudo systemctl daemon-reload
sudo systemctl restart network-optimizer-agent.service

# 5. Verify service is running
sudo systemctl status network-optimizer-agent.service
```

#### Manual Installation

```bash
# 1. Create directory
sudo mkdir -p /opt/network-optimizer-agent

# 2. Copy script
sudo cp network-optimizer-agent.sh /opt/network-optimizer-agent/
sudo chmod +x /opt/network-optimizer-agent/network-optimizer-agent.sh

# 3. Install dependencies
sudo apt-get update
sudo apt-get install -y bc curl

# 4. Copy and edit service file
sudo cp network-optimizer-agent.service /etc/systemd/system/
sudo nano /etc/systemd/system/network-optimizer-agent.service
# Edit Environment variables

# 5. Enable and start service
sudo systemctl daemon-reload
sudo systemctl enable network-optimizer-agent.service
sudo systemctl start network-optimizer-agent.service

# 6. Check status
sudo systemctl status network-optimizer-agent.service
```

---

### Configuration

#### Enable Docker Stats Collection

Edit `/etc/systemd/system/network-optimizer-agent.service`:

```ini
Environment="COLLECT_DOCKER_STATS=true"
```

Then reload and restart:

```bash
sudo systemctl daemon-reload
sudo systemctl restart network-optimizer-agent.service
```

#### Monitor Multiple Network Interfaces

Edit the service file:

```ini
Environment="NETWORK_INTERFACES=eth0,eth1,wlan0"
```

---

### Monitoring

#### Check Service Status

```bash
# Service status
sudo systemctl status network-optimizer-agent.service

# Service logs (journalctl)
sudo journalctl -u network-optimizer-agent.service -f

# Agent log file
sudo tail -f /var/log/network-optimizer-agent.log
```

#### Manual Test Run

```bash
# Stop service
sudo systemctl stop network-optimizer-agent.service

# Run in dry run mode
sudo DRY_RUN=true DEBUG=true /opt/network-optimizer-agent/network-optimizer-agent.sh

# Start service again
sudo systemctl start network-optimizer-agent.service
```

---

## InfluxDB Setup

### Create Bucket and Token

```bash
# Using InfluxDB CLI
influx bucket create -n network-metrics -o your-org

# Create token with write access
influx auth create \
  --org your-org \
  --write-bucket network-metrics \
  --description "Network Optimizer Agent"

# Copy the generated token
```

### Query Metrics in InfluxDB

```flux
// Query TC rate over time
from(bucket: "network-metrics")
  |> range(start: -1h)
  |> filter(fn: (r) => r._measurement == "tc_rate")
  |> filter(fn: (r) => r.interface == "ifbeth2")

// Query system CPU usage
from(bucket: "network-metrics")
  |> range(start: -1h)
  |> filter(fn: (r) => r._measurement == "system_cpu_usage")

// Query network throughput
from(bucket: "network-metrics")
  |> range(start: -1h)
  |> filter(fn: (r) => r._measurement == "network_rx_bytes" or r._measurement == "network_tx_bytes")
  |> derivative(unit: 1s, nonNegative: true)
```

---

## Grafana Dashboard

### Sample Dashboard Panels

#### TC Rate Over Time
```
Query: tc_rate (interface=ifbeth2)
Visualization: Time series graph
```

#### System Resources
```
Query: system_cpu_usage, system_memory_usage_pct
Visualization: Gauge or time series
```

#### Network Throughput
```
Query: derivative(network_rx_bytes), derivative(network_tx_bytes)
Visualization: Time series graph
Units: bytes/sec → Mbps
```

#### Ping Latency (if collected separately)
```
Query: ping_latency
Visualization: Time series with threshold lines
```

---

## Advanced Topics

### Customizing Blending Algorithm

The speedtest blending algorithm can be tuned in `sqm-manager.sh`:

```bash
# Current: 60/40 blend when within threshold, 80/20 when below
if [ "$download_speed_mbps" -ge "$threshold" ]; then
    blended_speed=$(echo "scale=0; ($baseline * 0.6 + $measured * 0.4) / 1" | bc)
else
    blended_speed=$(echo "scale=0; ($baseline * 0.8 + $measured * 0.2) / 1" | bc)
fi

# Alternative: Always favor baseline heavily (90/10)
blended_speed=$(echo "scale=0; ($baseline * 0.9 + $measured * 0.1) / 1" | bc)

# Alternative: Equal weighting (50/50)
blended_speed=$(echo "scale=0; ($baseline * 0.5 + $measured * 0.5) / 1" | bc)
```

### Multi-WAN Support

To support multiple WAN connections, duplicate the scripts and modify:

```bash
# WAN 1 (eth2/ifbeth2)
WAN_INTERFACE="eth2"
IFB_INTERFACE="ifbeth2"
SPEEDTEST_RESULTS_FILE="/data/sqm-speedtest-result-wan1.txt"

# WAN 2 (eth0/ifbeth0)
WAN_INTERFACE="eth0"
IFB_INTERFACE="ifbeth0"
SPEEDTEST_RESULTS_FILE="/data/sqm-speedtest-result-wan2.txt"
```

Then update cron jobs to run both scripts.

---

## License

MIT License - See LICENSE file for details

## Contributing

Contributions welcome! Please open an issue or pull request.

## Support

For issues, questions, or feature requests, please open a GitHub issue.

---

## Acknowledgments

- Based on SQM scripts from UniFiCloudGatewayCustomization
- Uses Ookla Speedtest CLI
- Inspired by bufferbloat.net and OpenWrt SQM

---

**Version**: 1.0
**Last Updated**: 2025-12-08
**Author**: Network Optimizer Agent
