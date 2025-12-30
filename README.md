# Network Optimizer for UniFi

**Expert-level network intelligence for your UniFi infrastructure.**

Network Optimizer analyzes your UniFi network configuration and provides actionable security audits, performance testing, adaptive bandwidth management, and monitoring—filling the gap between Ubiquiti's data and knowing whether your config is actually *good*.

## Features

### Security & Configuration Auditing
- **60+ audit checks** across firewall rules, VLANs, switch ports, DNS, and wireless settings
- **Weighted scoring system** (0-100) with Critical, Warning, and Info severity levels
- **DNS security analysis**: DoH configuration, DNS leak prevention, DoT blocking, per-interface DNS validation
- **Port security**: MAC filtering detection, unused port identification, camera/IoT VLAN placement
- **Firewall analysis**: Any-any rule detection, shadowed rules, inter-VLAN isolation
- **Hardening recognition**: Credits for security measures already in place
- **Issue management**: Dismiss false positives, restore dismissed issues
- **PDF report export** for documentation or client delivery

### Adaptive SQM (Smart Queue Management)
- **Dual-WAN support** with independent configuration per interface
- **Connection profiles** for DOCSIS, Fiber, Wireless, Starlink, Cellular with optimized defaults
- **Speedtest-based adjustment**: Scheduled tests (morning/evening) adjust rates automatically
- **Ping-based adjustment**: Real-time latency monitoring every 5 minutes
- **One-click deployment** to UDM/UCG gateways with persistence via UDM Boot
- **Live status dashboard** showing effective rates, last adjustments, and baselines

### LAN Speed Testing
- **Gateway-to-device testing** using iperf3 between your gateway and APs/switches
- **Auto-discovery** of UniFi devices from your controller
- **Custom device support** for non-UniFi endpoints with per-device SSH credentials
- **Network path analysis** correlating results with hop count and infrastructure
- **Test history** with paginated results and performance trends

### Cellular Modem Monitoring
- **5G/LTE device support** for U-LTE and U5G-Max
- **Signal metrics**: RSSI, RSRP, RSRQ, SINR
- **Cell tower information** and connection status
- **Dashboard integration** with real-time signal display

### Dashboard & Monitoring
- **Real-time network health** with device counts and security score
- **SQM status** showing active WAN configurations
- **Alert system** with recent issues and dismissal
- **Device grid** showing all connected infrastructure
- **Speed test history** with recent results

### Agent Deployment
- **UDM/UCG Gateway Agent**: SQM metrics, speedtest, latency monitoring
- **Linux System Agent**: CPU, memory, disk, network, optional Docker metrics
- **SNMP Poller**: Switch and AP metrics collection
- **SSH-based deployment** with connection testing
- **Agent health monitoring** with heartbeat tracking

## Requirements

- **UniFi Controller**: UCG-Ultra, UCG-Max, UDM, UDM Pro, UDM SE, or standalone controller
- **Docker**: Any platform supporting Docker (Linux, macOS, Windows, Synology, Unraid, etc.)
- **Network Access**: HTTPS access to your UniFi controller API
- **For SQM**: SSH access to your gateway

## Quick Start

### macOS

```bash
git clone https://github.com/your-org/network-optimizer.git
cd network-optimizer/docker
docker compose -f docker-compose.macos.yml build
docker compose -f docker-compose.macos.yml up -d
```

Open http://localhost:8042 (wait ~60 seconds for startup)

### Linux / Windows

```bash
git clone https://github.com/your-org/network-optimizer.git
cd network-optimizer/docker
cp .env.example .env  # Optional: edit to set APP_PASSWORD
docker compose up -d
```

Open http://localhost:8042

### First Run

1. Go to **Settings** and enter your UniFi controller URL (e.g., `https://192.168.1.1`)
2. Use **local-only** credentials (create a local admin, not your Ubiquiti SSO account)
3. Click **Connect** to authenticate
4. Navigate to **Audit** to run your first security scan

## Project Structure

```
├── src/
│   ├── NetworkOptimizer.Web        # Blazor web UI
│   ├── NetworkOptimizer.Audit      # Security audit engine (60+ checks)
│   ├── NetworkOptimizer.UniFi      # UniFi API client
│   ├── NetworkOptimizer.Storage    # SQLite database & models
│   ├── NetworkOptimizer.Monitoring # SNMP/SSH polling
│   ├── NetworkOptimizer.Sqm        # Adaptive bandwidth management
│   ├── NetworkOptimizer.Agents     # Agent deployment & health
│   └── NetworkOptimizer.Reports    # PDF/Markdown generation
├── docker/                         # Docker deployment files
└── docs/                           # Additional documentation
```

## Technology Stack

- **.NET 9** with Blazor Server
- **SQLite** for local data storage
- **Docker** for cross-platform deployment
- **iperf3** for throughput testing
- **SSH.NET** for gateway management
- RESTful integration with UniFi Controller API

## Current Status

**Alpha** - Core features working, actively seeking testers.

### What Works
- UniFi controller authentication (UniFi OS and standalone)
- Security audit with 60+ checks, scoring, and PDF reports
- Adaptive SQM configuration and deployment (dual-WAN)
- LAN speed testing with path analysis
- Cellular modem monitoring (5G/LTE)
- Agent deployment framework
- Dashboard with real-time status

### In Progress
- Time-series metrics with InfluxDB/Grafana
- Multi-site support for MSPs

## Contributing

We welcome testers and contributors! Please:

1. Report issues via GitHub Issues
2. Include your UniFi device models and controller version
3. Attach relevant logs (sanitize any credentials/IPs first)

## License

**Proprietary / All Rights Reserved**

Copyright (c) 2024-2025 TJ Van Cott. All rights reserved.

This software is provided for evaluation and testing purposes only. Commercial use, redistribution, or modification requires explicit written permission from the author.

## Support

- **Issues**: [GitHub Issues](https://github.com/your-org/network-optimizer/issues)
- **Documentation**: See `docs/` folder and component READMEs

---

*Built for the UniFi community by network enthusiasts who wanted more from their gear.*
