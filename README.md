# Network Optimizer for UniFi

**Expert-level network intelligence for your UniFi infrastructure.**

Network Optimizer analyzes your UniFi network configuration and provides actionable security audits, performance insights, and monitoring—filling the gap between Ubiquiti's data and knowing whether your config is actually *good*.

## Features

### Security & Configuration Auditing
- **60+ audit checks** across firewall rules, VLANs, switch ports, and wireless settings
- Weighted scoring system (Critical → Info) with actionable recommendations
- Detects misconfigurations like open management ports, missing inter-VLAN rules, legacy protocols
- Export PDF reports for documentation or client delivery

### Network Device Speed Testing
- Run iperf3 tests between your gateway and network devices (APs, switches)
- Verify actual throughput vs. link speed expectations
- Identify bottlenecks in your wired infrastructure

### Cellular Modem Monitoring
- Monitor UniFi LTE/5G devices (U-LTE, U5G-Max)
- Track signal strength (RSSI, RSRP, RSRQ, SINR)
- Cell tower information and connection status

### Unified Dashboard
- Real-time network health overview
- Device status and connectivity
- Recent audit findings at a glance

## Screenshots

*Coming soon*

## Requirements

- **UniFi Controller**: UCG-Ultra, UCG-Max, UDM, UDM Pro, UDM SE, or standalone controller
- **Docker**: Any platform supporting Docker (Linux, macOS, Windows, Synology, Unraid, etc.)
- **Network Access**: HTTPS access to your UniFi controller API

## Quick Start

### macOS

```bash
git clone https://github.com/your-org/network-optimizer.git
cd network-optimizer/docker
docker compose -f docker-compose.macos.yml build
docker compose -f docker-compose.macos.yml up -d
```

Open http://localhost:8042 (wait ~60 seconds for startup)

### Linux / Windows (Docker Compose)

1. Clone the repository:
   ```bash
   git clone https://github.com/your-org/network-optimizer.git
   cd network-optimizer/docker
   ```

2. Create your environment file:
   ```bash
   cp .env.example .env
   # Edit .env if needed
   ```

3. Start the container:
   ```bash
   docker compose up -d
   ```

4. Open http://localhost:8042 in your browser

5. Go to **Settings** and configure your UniFi controller connection

### First Run

1. Enter your UniFi controller URL (e.g., `https://192.168.1.1`)
2. Use **local-only** credentials (create a local admin, not your Ubiquiti SSO account)
3. Click **Connect** to authenticate
4. Navigate to **Audit** to run your first security scan

## Project Structure

```
├── src/
│   ├── NetworkOptimizer.Web      # Blazor web UI
│   ├── NetworkOptimizer.Audit    # Security audit engine (60+ checks)
│   ├── NetworkOptimizer.UniFi    # UniFi API client
│   ├── NetworkOptimizer.Storage  # SQLite database & models
│   └── NetworkOptimizer.Monitoring # SNMP/SSH polling
├── docker/                       # Docker deployment files
└── docs/                         # Additional documentation
```

## Technology Stack

- **.NET 9** with Blazor Server
- **SQLite** for local data storage
- **Docker** for cross-platform deployment
- RESTful integration with UniFi Controller API

## Current Status

**Alpha** - Core features working, actively seeking testers.

### What Works
- UniFi controller authentication (UniFi OS and standalone)
- Security audit with 60+ checks and scoring
- PDF report generation
- Device SSH connectivity testing
- Cellular modem monitoring
- Speed testing between gateway and devices

### Coming Soon
- Dynamic SQM management (adaptive bandwidth optimization)
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
