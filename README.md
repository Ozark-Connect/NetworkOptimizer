# Network Optimizer for UniFi

You've got a UniFi network. Maybe you've spent hours configuring VLANs, firewall rules, and port security. But here's the thing: Ubiquiti gives you all this data and configuration power, but it doesn't tell you whether your setup is actually *good*. Are your firewall rules doing what you think? Is that IoT VLAN really isolated? Is your DNS leaking to your ISP despite that Pi-hole you set up?

Network Optimizer fills that gap - analyzing your UniFi configuration and giving you actionable answers instead of just more data to stare at.

## What It Does

### Security & Configuration Auditing

The audit engine runs 60+ checks across your entire UniFi setup and scores your network 0-100. Not just "you have a firewall" but actually looking at what your rules do:

- DNS security analysis: DoH configuration, DNS leak prevention, DoT blocking, per-interface DNS validation (that last one catches a lot of people - your WAN DNS settings might not be what you think they are)
- Port security: finds access ports without MAC filtering, flags unused ports that aren't explicitly disabled, checks whether your cameras and IoT devices are actually on the VLANs you intended
- Firewall analysis: detects overly permissive any-any rules, finds shadowed rules that never fire, verifies inter-VLAN isolation
- Hardening recognition: gives you credit for security measures you've already implemented

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
- Docker: Linux, macOS, Windows, Synology, Unraid - whatever you've got
- Network access to your UniFi controller API (HTTPS)
- For SQM features: SSH access to your gateway

## Quick Start

### macOS

```bash
git clone https://github.com/your-org/network-optimizer.git
cd network-optimizer/docker
docker compose -f docker-compose.macos.yml build
docker compose -f docker-compose.macos.yml up -d
```

Open http://localhost:8042 (give it about 60 seconds to start up)

### Linux / Windows

```bash
git clone https://github.com/your-org/network-optimizer.git
cd network-optimizer/docker
cp .env.example .env  # Optional - edit to set APP_PASSWORD
docker compose up -d
```

Open http://localhost:8042

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
- Security audit with 60+ checks, scoring, PDF reports
- Adaptive SQM configuration and deployment (dual-WAN)
- LAN speed testing with path analysis
- Cellular modem monitoring
- Agent deployment
- Dashboard with real-time status

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

- Issues: [GitHub Issues](https://github.com/your-org/network-optimizer/issues)
- Documentation: See `docs/` folder and component READMEs

---

*Built for the UniFi community by someone who wanted more from their gear.*
