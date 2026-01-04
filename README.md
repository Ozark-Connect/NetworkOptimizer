# Network Optimizer for UniFi

> **Early Access Testers:** This project is under active development. Until official beta releases are cut, please **pull from `main` regularly** to get the latest fixes and features. Breaking changes may occur between updates.

## New: Client-Based LAN Speed Testing

Test LAN speeds from any device on your network - phones, tablets, laptops - without SSH access. Run browser-based speed tests powered by [OpenSpeedTest™](https://openspeedtest.com) or use iperf3 clients; results are automatically collected and displayed with device identification, network path visualization, and performance metrics.

![Client Speed Test](docs/images/client-speed-test.png)

---

You've set up VLANs, configured firewall rules, maybe even deployed a Pi-hole for DNS filtering. The UniFi controller gives you all this power, but it never actually tells you whether your configuration is any good. Are your firewall rules doing what you think they're doing? Is that IoT VLAN actually isolated, or did you miss something? When a device bypasses your DNS settings and phones home directly, would you even know?

Network Optimizer answers those questions. It connects to your UniFi controller, analyzes your configuration, and tells you what's working, what's broken, and what you should fix. No more guessing.

## What It Does

### Security Auditing

The audit engine runs 39 security checks across four categories and scores your network 0-100. This isn't a checkbox audit that just confirms you have a firewall; it actually analyzes what your rules do and whether they're doing it correctly.

Firewall analysis catches the subtle stuff: rules that shadow each other, allow rules that subvert your deny rules, orphaned references to networks that no longer exist. VLAN security checks whether your IoT devices and cameras are actually on the networks you intended (using UniFi fingerprints, MAC OUI lookup, and port naming patterns). DNS security validates your DoH configuration, checks for bypass routes, and verifies that your WAN interface DNS settings match what you configured. Port security looks at MAC restrictions, port isolation, and whether you've left unused ports enabled.

You get a score, a breakdown by severity (critical, recommended, informational), and specific recommendations for each issue. Dismiss false positives if your setup is intentional, export PDF reports for documentation, track your score over time.

### Adaptive SQM

If you're on cable, DSL, or cellular, you know bufferbloat. That lag spike when someone starts a download or joins a video call. SQM fixes it, but setting the bandwidth limits correctly is a guessing game; too high and SQM can't shape traffic effectively, too low and you're leaving speed on the table.

Network Optimizer handles this automatically. It supports dual-WAN with independent configuration per interface, connection profiles tuned for DOCSIS, fiber, wireless, Starlink, and cellular (each has different characteristics that matter). Scheduled speedtests adjust your rates based on actual measured performance. Latency monitoring backs off when congestion appears. One-click deployment pushes the configuration to your UDM or UCG gateway with persistence through reboots.

### LAN Speed Testing

Ever wonder if that new switch is actually delivering gigabit speeds? Or whether the cable run to the shop is the bottleneck? Network Optimizer runs iperf3 tests between your gateway and network devices, auto-discovers UniFi equipment from your controller, supports custom devices with per-device SSH credentials, and correlates results with hop count and infrastructure path. Test history lets you track performance over time.

### Client Speed Testing

Test LAN speeds from any device without SSH access. Open a browser on your phone, tablet, or laptop and run a speed test; results are automatically recorded with device identification. For CLI users, the bundled iperf3 server accepts client connections and logs results. See [Client Speed Testing](docker/DEPLOYMENT.md#client-speed-testing-optional) in the deployment guide.

### Cellular Modem Monitoring

If you're running a U-LTE or U5G-Max for backup (or primary) connectivity, you can monitor signal quality from the dashboard: RSSI, RSRP, RSRQ, SINR, cell tower info, and connection status.

### Coming Soon

Time-series metrics with historical trending and alerting. Cable modem stats (signal levels, uncorrectables, T3/T4 timeouts) for those of you fighting with your ISP about line quality.

## Requirements

**Basic (Security Audit only):**
- UniFi controller: UCG-Ultra, UCG-Max, UDM, UDM Pro, UDM SE, or standalone controller
- Network access to your UniFi controller API (HTTPS)

**Full Functionality (Adaptive SQM, LAN Speed Testing):**
- SSH access enabled on your UniFi gateway and devices (configured via web interface, not mobile app)
- **Console SSH:** Settings > Control Plane > Console > SSH
- **Device SSH:** UniFi Devices > Device Updates and Settings > Device SSH Settings
- See [Deployment Guide](docker/DEPLOYMENT.md#unifi-ssh-configuration) for detailed instructions (UniFi Network 9.5+)

Without SSH access, Security Audit works fully, but you cannot run gateway/device LAN speed tests or deploy Adaptive SQM configurations.

## Installation

| Platform | Method | Guide |
|----------|--------|-------|
| Linux Server | Docker (recommended) | [Deployment Guide](docker/DEPLOYMENT.md) |
| Synology/QNAP/Unraid | Docker | [NAS Deployment](docker/DEPLOYMENT.md#nas-deployment) |
| macOS | Native (best performance) | [macOS Native](docker/NATIVE-DEPLOYMENT.md#macos-deployment) |
| Linux | Native (no Docker) | [Linux Native](docker/NATIVE-DEPLOYMENT.md#linux-deployment) |
| Windows | Native | [Windows Native](docker/NATIVE-DEPLOYMENT.md#windows-deployment) |

Docker Desktop on macOS and Windows adds virtualization overhead that limits network throughput. For accurate multi-gigabit speed testing, use native deployment.

### Quick Start (Linux Docker)

```bash
git clone https://github.com/Ozark-Connect/NetworkOptimizer.git
cd NetworkOptimizer/docker
cp .env.example .env
docker compose up -d

# Check logs for the auto-generated admin password
docker compose logs network-optimizer | grep -A5 "FIRST-RUN"
```

Open http://localhost:8042

### First Run

1. Go to Settings and enter your UniFi controller URL
2. Create a local admin account on your controller (don't use your Ubiquiti SSO login)
3. Click Connect to authenticate
4. Navigate to Audit to run your first security scan

## Project Structure

```
src/
├── NetworkOptimizer.Web        # Blazor web UI
├── NetworkOptimizer.Audit      # Security audit engine
├── NetworkOptimizer.UniFi      # UniFi API client
├── NetworkOptimizer.Storage    # SQLite database
├── NetworkOptimizer.Monitoring # SNMP/SSH polling
├── NetworkOptimizer.Sqm        # Adaptive bandwidth management
├── NetworkOptimizer.Agents     # Agent deployment
└── NetworkOptimizer.Reports    # PDF/Markdown generation
```

## Tech Stack

.NET 10, Blazor Server, SQLite, iperf3, SSH.NET, QuestPDF

## Status

Core features are working. Actively looking for testers.

What works: UniFi controller authentication (UniFi OS and standalone), security auditing with 39 checks and PDF reports, adaptive SQM with dual-WAN support, LAN speed testing with path analysis, cellular modem monitoring.

In progress: Time-series metrics, cable modem monitoring, multi-site support.

## Contributing

If you find issues, report them via GitHub Issues. Include your UniFi device models and controller version. Sanitize credentials and IPs before attaching logs.

## License

Business Source License 1.1

**Licensor:** Ozark Connect

**Licensed Work:** Network Optimizer for UniFi

**Personal Use:** You may use the Licensed Work for personal, non-commercial purposes on up to three sites.

**Commercial Use:** Use by managed service providers (MSPs), network installers, IT consultants, or any entity using this software in the delivery of paid services requires a commercial license.

**Change Date:** January 1, 2028

**Change License:** Apache License 2.0

For commercial licensing inquiries, contact tj@ozarkconnect.net.

© 2026 Ozark Connect

## Support

- Issues: [GitHub Issues](https://github.com/Ozark-Connect/NetworkOptimizer/issues)
- Documentation: See component READMEs in `src/` and `docker/`
