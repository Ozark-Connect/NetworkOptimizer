# Network Optimizer for UniFi

You've got a UniFi network. Maybe you've spent hours configuring VLANs, firewall rules, and port security. But here's the thing: Ubiquiti gives you all this data and configuration power, but it doesn't tell you whether your setup is actually *good*. Are your firewall rules doing what you think? Is that IoT VLAN really isolated? Is your DNS leaking to your ISP despite that Pi-hole you set up?

Network Optimizer fills that gap - analyzing your UniFi configuration and giving you actionable answers instead of just more data to stare at.

## What It Does

### Security & Configuration Auditing

The audit engine runs **39 security checks** across 4 categories, scoring your network 0-100. Not just "you have a firewall" but actually analyzing what your rules do:

| Category | Checks | What It Analyzes |
|----------|--------|------------------|
| **Firewall** | 8 | Any-any rules, shadowed rules, permissive patterns, orphaned references, inter-VLAN isolation |
| **VLAN Security** | 18 | Device placement (IoT/cameras on wrong VLANs), network isolation, management access, routing config |
| **DNS Security** | 10 | DoH configuration, DNS leak prevention (port 53/853 blocking), WAN DNS validation, provider detection |
| **Port Security** | 3 | MAC restrictions, port isolation, unused port hardening |

**Severity levels:** Critical (12 rules), Recommended (16 rules), Informational (11 rules)

Key capabilities:
- **Device detection**: Multi-tier classification using UniFi fingerprints, MAC OUI lookup, and port naming patterns
- **Firewall intelligence**: Detects rule shadowing, subversion, and ordering issues that cause unintended behavior
- **DNS leak prevention**: Validates DoH config, checks for bypass routes, verifies WAN interface DNS settings
- **VLAN validation**: Confirms cameras and IoT devices are actually on the VLANs you intended

You can dismiss false positives, export PDF reports for documentation, and track your score over time.

### Adaptive SQM (Smart Queue Management)

If you've got cable, DSL, or cellular internet, you probably know the pain of bufferbloat - that frustrating lag spike when someone starts a download or video call. SQM fixes this, but setting the right bandwidth limits is a guessing game. Set them too high and SQM can't do its job; too low and you're leaving speed on the table.

Network Optimizer handles this automatically:

- Dual-WAN support with independent config per interface
- Connection profiles for DOCSIS, Fiber, Wireless, Starlink, Cellular (each has different characteristics that matter)
- Speedtest-based adjustment: scheduled tests in the morning and evening adjust your rates based on actual measured speeds
- Ping-based adjustment: monitors latency every 5 minutes and backs off when congestion appears
- One-click deployment to UDM/UCG gateways with persistence through reboots via UDM Boot

The dashboard shows your current effective rates, last speedtest results, and ping adjustments in real-time.

### LAN Speed Testing

Ever wonder if that new switch is actually giving you gigabit speeds? Or whether the cable run to the warehouse is the bottleneck? Network Optimizer runs iperf3 tests between your gateway and network devices:

- Auto-discovers UniFi devices from your controller
- Custom device support for non-UniFi endpoints (with per-device SSH credentials)
- Path analysis that correlates results with hop count and infrastructure
- Test history so you can track performance over time

### Cellular Modem Monitoring

If you're running a U-LTE or U5G-Max for backup (or primary) connectivity, you can monitor signal quality right from the dashboard: RSSI, RSRP, RSRQ, SINR, cell tower info, and connection status.

### Agent Deployment

For more comprehensive monitoring, you can deploy lightweight agents:

- UDM/UCG Gateway Agent: SQM metrics, speedtest execution, latency monitoring
- Linux System Agent: CPU, memory, disk, network stats, optional Docker metrics
- SNMP Poller: switch and AP metrics collection

Agents deploy via SSH with connection testing built in.

## Requirements

- UniFi Controller: UCG-Ultra, UCG-Max, UDM, UDM Pro, UDM SE, or standalone controller
- Network access to your UniFi controller API (HTTPS)
- For SQM features: SSH access to your gateway
- See deployment options below for host requirements

## Installation

Choose your deployment method:

| Platform | Method | Guide |
|----------|--------|-------|
| Linux Server | Docker (recommended) | [Deployment Guide](docker/DEPLOYMENT.md#1-linux-docker-recommended) |
| Synology/QNAP/Unraid | Docker | [NAS Deployment](docker/DEPLOYMENT.md#2-nas-deployment-docker) |
| macOS | Native (best performance) | [macOS Native](docker/NATIVE-DEPLOYMENT.md#macos-deployment) |
| macOS | Docker | [Deployment Guide](docker/DEPLOYMENT.md) |
| Linux | Native (no Docker) | [Linux Native](docker/NATIVE-DEPLOYMENT.md#linux-deployment) |
| Windows | Native | [Windows Native](docker/NATIVE-DEPLOYMENT.md#windows-deployment) |

> **Note:** Docker Desktop (macOS/Windows) adds virtualization overhead that can limit network throughput. For accurate multi-gigabit speed testing, use [native deployment](docker/NATIVE-DEPLOYMENT.md).

### Quick Start (Linux Docker)

```bash
git clone https://github.com/Ozark-Connect/NetworkOptimizer.git
cd network-optimizer/docker
cp .env.example .env  # Optional - edit to set APP_PASSWORD
docker compose up -d
```

Open http://localhost:8080

### First Run

1. Go to Settings and enter your UniFi controller URL (e.g., `https://192.168.1.1`)
2. Use local-only credentials - create a local admin account on your controller, don't use your Ubiquiti SSO login
3. Click Connect to authenticate
4. Navigate to Audit to run your first security scan

## Project Structure

```
├── src/
│   ├── NetworkOptimizer.Web        # Blazor web UI
│   ├── NetworkOptimizer.Audit      # Security audit engine
│   ├── NetworkOptimizer.UniFi      # UniFi API client
│   ├── NetworkOptimizer.Storage    # SQLite database & models
│   ├── NetworkOptimizer.Monitoring # SNMP/SSH polling
│   ├── NetworkOptimizer.Sqm        # Adaptive bandwidth management
│   ├── NetworkOptimizer.Agents     # Agent deployment & health
│   └── NetworkOptimizer.Reports    # PDF/Markdown generation
├── docker/                         # Docker deployment files
└── docs/                           # Additional documentation
```

## Tech Stack

- .NET 9 with Blazor Server
- SQLite for local storage
- Docker for deployment
- iperf3 for throughput testing
- SSH.NET for gateway management
- UniFi Controller API integration

## Current Status

Alpha - core features are working, actively looking for testers.

What works:
- UniFi controller auth (UniFi OS and standalone)
- Security audit with 39 checks across firewall, VLAN, DNS, and port security - with scoring and PDF reports
- Adaptive SQM configuration and deployment (dual-WAN)
- LAN speed testing with L2 path analysis and device icons
- Cellular modem monitoring (U-LTE, U5G-Max)
- Dashboard with real-time status and device images

In progress:
- Time-series metrics with InfluxDB/Grafana
- Multi-site support for MSPs

## Contributing

Testers and contributors welcome. If you find issues:

1. Report via GitHub Issues
2. Include your UniFi device models and controller version
3. Attach relevant logs (sanitize credentials and IPs first)

## License

Proprietary / All Rights Reserved

Copyright (c) 2025 SeaTurtle. All rights reserved.

This software is provided for evaluation and testing purposes only. Commercial use, redistribution, or modification requires explicit written permission.

## Support

- Issues: [GitHub Issues](https://github.com/Ozark-Connect/NetworkOptimizer/issues)
- Documentation: See `docs/` folder and component READMEs

---

*Built for the UniFi community by someone who wanted more from their gear.*
