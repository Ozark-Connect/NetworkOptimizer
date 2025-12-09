# Quick Start Guide - Network Optimizer Agent

## For UniFi Dream Machine / Cloud Gateway

### 1. Prepare Configuration

Edit the scripts on your computer before uploading:

```bash
cd agents/udm

# Edit SQM Manager
nano sqm-manager.sh
# Set: WAN_INTERFACE, IFB_INTERFACE, MAX_DOWNLOAD_SPEED, MIN_DOWNLOAD_SPEED

# Edit Ping Monitor
nano sqm-ping-monitor.sh
# Set: ISP_PING_HOST, BASELINE_LATENCY, LATENCY_THRESHOLD

# Edit Metrics Collector
nano metrics-collector.sh
# Set: INFLUXDB_URL, INFLUXDB_TOKEN, INFLUXDB_ORG, INFLUXDB_BUCKET
```

### 2. Upload to Gateway

```bash
# From your computer
scp *.sh root@<gateway-ip>:/tmp/
```

### 3. Install on Gateway

```bash
# SSH to gateway
ssh root@<gateway-ip>

# Run installer
cd /tmp
chmod +x install.sh
./install.sh
```

### 4. Verify Installation

```bash
# Check logs
tail -f /var/log/network-optimizer-agent-setup.log

# Check cron jobs
crontab -l

# View current TC configuration
tc class show dev ifbeth2 | grep "class htb"

# Test manually
/data/network-optimizer-agent/sqm-manager.sh
```

### 5. Monitor

```bash
# SQM manager log
tail -f /var/log/sqm-manager.log

# Ping monitor log
tail -f /var/log/sqm-ping-monitor.log

# Metrics collector log
tail -f /var/log/metrics-collector.log
```

---

## For Linux Systems

### 1. Configure Service

```bash
cd agents/linux

# Edit systemd service file
nano network-optimizer-agent.service

# Update these environment variables:
# - INFLUXDB_URL
# - INFLUXDB_TOKEN
# - INFLUXDB_ORG
# - INFLUXDB_BUCKET
# - NETWORK_INTERFACES
# - COLLECT_DOCKER_STATS (set to "true" if you want Docker stats)
```

### 2. Install

```bash
# Run installer
chmod +x install.sh
sudo ./install.sh
```

### 3. Apply Configuration

```bash
# Reload systemd
sudo systemctl daemon-reload

# Restart service
sudo systemctl restart network-optimizer-agent.service
```

### 4. Verify

```bash
# Check service status
sudo systemctl status network-optimizer-agent.service

# View logs
sudo journalctl -u network-optimizer-agent.service -f

# Or view log file
sudo tail -f /var/log/network-optimizer-agent.log
```

---

## Common Configuration Examples

### UDM/UCG - Cable Internet (Xfinity, Spectrum, Cox)

```bash
# sqm-manager.sh
WAN_INTERFACE="eth2"
IFB_INTERFACE="ifbeth2"
MAX_DOWNLOAD_SPEED="285"      # Your plan speed * 0.95
MIN_DOWNLOAD_SPEED="190"      # ~67% of max
DOWNLOAD_SPEED_MULTIPLIER="1.05"

# sqm-ping-monitor.sh
ISP_PING_HOST="your-isp-gateway-ip"  # Find via: traceroute 8.8.8.8
BASELINE_LATENCY="10.5"       # Measure via: ping -c 100 <host>
LATENCY_THRESHOLD="3.0"       # ~30% of baseline
```

### UDM/UCG - Fiber Internet (Google Fiber, AT&T Fiber)

```bash
# sqm-manager.sh
MAX_DOWNLOAD_SPEED="950"      # For 1Gbps plan
MIN_DOWNLOAD_SPEED="700"
DOWNLOAD_SPEED_MULTIPLIER="1.02"  # Less overhead needed

# sqm-ping-monitor.sh
BASELINE_LATENCY="5.0"        # Fiber typically very low latency
LATENCY_THRESHOLD="1.5"
```

### UDM/UCG - Satellite Internet (Starlink)

```bash
# sqm-manager.sh
MAX_DOWNLOAD_SPEED="200"      # Starlink varies widely
MIN_DOWNLOAD_SPEED="50"
DOWNLOAD_SPEED_MULTIPLIER="1.10"  # More overhead due to variability

# sqm-ping-monitor.sh
BASELINE_LATENCY="45.0"       # Starlink typically 30-60ms
LATENCY_THRESHOLD="15.0"      # Allow more variance
LATENCY_DECREASE="0.95"       # More aggressive decrease
```

### Linux - Home Server

```bash
# network-optimizer-agent.service
Environment="INFLUXDB_URL=http://192.168.1.100:8086"
Environment="INFLUXDB_TOKEN=your-token-here"
Environment="INFLUXDB_ORG=home"
Environment="INFLUXDB_BUCKET=network-metrics"
Environment="COLLECTION_INTERVAL=60"
Environment="COLLECT_SYSTEM_STATS=true"
Environment="COLLECT_DOCKER_STATS=true"
Environment="COLLECT_NETWORK_STATS=true"
Environment="NETWORK_INTERFACES=eth0"
```

### Linux - Multi-Interface Router

```bash
# network-optimizer-agent.service
Environment="NETWORK_INTERFACES=eth0,eth1,eth2,wlan0"
Environment="COLLECTION_INTERVAL=30"  # More frequent for router
```

---

## Troubleshooting Quick Reference

### UDM/UCG: "speedtest command not found"

```bash
apt-get remove -y speedtest
curl -s https://packagecloud.io/install/repositories/ookla/speedtest-cli/script.deb.sh | bash
apt-get install -y speedtest
```

### UDM/UCG: "tc command failed"

```bash
# Enable SQM in UniFi UI first:
# Network > Settings > Internet > WAN > Smart Queue Management

# Verify IFB device exists
ip link show ifbeth2

# If missing, check SQM settings in UI
```

### UDM/UCG: Scripts not running automatically

```bash
# Check cron jobs
crontab -l

# If missing, run setup script again
/data/on_boot.d/50-network-optimizer-agent.sh
```

### Linux: Service won't start

```bash
# Check service status
sudo systemctl status network-optimizer-agent.service

# Check for configuration errors
sudo journalctl -u network-optimizer-agent.service -n 50

# Test script manually
sudo /opt/network-optimizer-agent/network-optimizer-agent.sh
```

### InfluxDB: Metrics not appearing

```bash
# Test in dry run mode
DRY_RUN=true DEBUG=true /path/to/metrics-collector.sh

# Test InfluxDB connection
curl -i -X POST \
  "http://your-influxdb:8086/api/v2/write?org=your-org&bucket=network-metrics&precision=ns" \
  -H "Authorization: Token your-token" \
  -H "Content-Type: text/plain" \
  --data-binary "test,host=test value=1 $(date +%s)000000000"

# Verify token has write permissions
# Check InfluxDB logs
```

---

## Next Steps

1. **Monitor for 24-48 hours** to establish baseline behavior
2. **Tune parameters** based on your specific network
3. **Set up Grafana dashboards** to visualize metrics
4. **Adjust baseline data** after collecting a week of speedtest results
5. **Fine-tune latency thresholds** based on observed patterns

---

## Support Resources

- Full documentation: `README.md`
- Reference scripts: `C:\Users\tjvc4\OneDrive\PersonalProjects\UniFiCloudGatewayCustomization\boot-scripts`
- InfluxDB documentation: https://docs.influxdata.com/
- UniFi documentation: https://help.ui.com/

---

**Remember**: Always test in a non-production environment first, or during off-peak hours!
