<p align="center">
  <img src="docs/images/app-logo-v2.png" alt="Network Optimizer" width="200">
</p>

# Network Optimizer for UniFi

[![GitHub Release](https://img.shields.io/github/v/release/Ozark-Connect/NetworkOptimizer)](https://github.com/Ozark-Connect/NetworkOptimizer/releases)
[![Docker Pulls](https://img.shields.io/badge/docker_pulls-62k-blue?logo=docker)](https://github.com/orgs/Ozark-Connect/packages?repo_name=NetworkOptimizer)
[![Windows Downloads](https://img.shields.io/github/downloads/Ozark-Connect/NetworkOptimizer/total?label=windows%20downloads)](https://github.com/Ozark-Connect/NetworkOptimizer/releases)
[![GitHub last commit](https://img.shields.io/github/last-commit/Ozark-Connect/NetworkOptimizer)](https://github.com/Ozark-Connect/NetworkOptimizer/commits)
[![GitHub Stars](https://img.shields.io/github/stars/Ozark-Connect/NetworkOptimizer)](https://github.com/Ozark-Connect/NetworkOptimizer/stargazers)
[![License](https://img.shields.io/badge/license-BSL_1.1-green)](https://github.com/Ozark-Connect/NetworkOptimizer/blob/main/LICENSE)

## THANK YOU to all of my Sponsors

Genuinely, thank you so much to everybody for taking the time to use Network Optimizer and have it find a place on your network(s). It really means a lot to receive all of the bug reports, feature requests, feedback, support, and donations from everybody. Totally a whole new experience from writing code in a dayjob, and it greatly motivates me to keep on going!

## New: HTTPS Reverse Proxy

Enable HTTPS with automatic Let's Encrypt certificates using the included [Traefik reverse proxy](https://github.com/Ozark-Connect/NetworkOptimizer-Proxy). It forces HTTP/1.1 for speed tests (HTTP/2 multiplexing skews results) while keeping HTTP/2 for the main app. Windows MSI users can enable Traefik as an optional feature during install. HTTPS also unlocks GPS-based tagging on your self-hosted Speed Test and Signal walk test data, since browsers require a secure context for location access.

## New: Threat Intelligence

Your UniFi gateway's IPS is blocking threats all day long, but the UniFi Console buries this data in a flat event log with no context. Threat Intelligence pulls those IPS events and actually analyzes them: who's attacking you, where they're coming from, what they're after, and whether it's random noise or a coordinated effort.

The exposure analysis is where it gets useful. It cross-references your port forwards with actual threat data, so you can see which of your exposed services are getting hammered and from where. Attack sequence detection watches for the same source IP progressing through kill chain stages (reconnaissance to exploitation to post-exploitation) and flags the ones that look like real campaigns rather than drive-by scanning. Geographic and ASN breakdowns show you which countries and networks are generating the most traffic against your infrastructure.

CrowdSec CTI integration adds reputation scoring and MITRE ATT&CK classification to each source IP, so you're not just looking at raw events - you know whether that IP has a history of malicious activity across the broader internet.

## New: Alerts & Scheduling

Set up automated speed tests and security audits on a schedule, and get notified when something goes wrong. The scheduling engine handles recurring WAN and LAN speed tests with configurable frequency and time windows, plus periodic security audits that track your score over time.

Alert rules watch for the things that matter: audit score drops, WAN speed degradation, LAN speed regression against recent baselines, IPS attack chains reaching active exploitation, and scheduled task failures. Each rule has configurable severity thresholds and cooldown periods so you're not drowning in noise. Threshold-based rules (like "alert me when WAN speed drops 40% below the recent average") let you tune sensitivity to your environment.

Delivery channels support email (SMTP with STARTTLS), Discord, Slack, Microsoft Teams, and generic webhooks. Low-priority alerts can be set to digest-only mode so they get bundled into a daily summary instead of pinging you every time your neighbor microwaves lunch and your 2.4 GHz channel gets congested.
## New: Client Performance

A per-device analytics dashboard for any client on your network. Pick a device and get live signal monitoring, speed test history with download/upload trends, latency and jitter charts, network path visualization showing every hop and bottleneck link, and a connection timeline tracking AP roams and disconnects. Walk around with the page open on your phone (over HTTPS) and it builds a GPS-based signal heatmap of your actual coverage. Three tabs - Speed, Signal, and Connection - give you everything you need to troubleshoot why a device is slow or unstable.

## New: Signal Map

Draw your building layout, place APs, and see a real-time RF propagation heatmap. The floor plan editor supports wall materials (drywall, concrete, glass, etc.), multi-floor buildings with cross-floor signal propagation, and per-AP antenna patterns pulled from your UniFi controller. Simulate TX power and antenna mode changes to see how they'd affect coverage before touching your actual config. Add planned APs to simulate coverage before buying or mounting hardware.

## New: WAN Speed Test

Test your internet connection speed directly from the server using Cloudflare's speed infrastructure. Measures download, upload, and loaded latency (bufferbloat detection) with per-WAN connection tracking. Results are displayed in a time-series chart with filter badges for multi-WAN setups, so you can compare providers over time.

## New: Wi-Fi Optimizer

Site health scoring, RF environment analysis, channel optimization, client stats, roaming tracking, band steering, and airtime fairness across twelve analysis tabs. All recommendations include specific UniFi UI paths for applying changes.​​​​​​​​​​​​​​​​

## New: Proxmox LXC Installation

Deploy on Proxmox with a single command. Run this on your Proxmox VE host:

```bash
bash -c "$(wget -qLO - https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/proxmox/install.sh)"
```

The interactive script creates a Debian 12 LXC container with Docker pre-configured. See [Proxmox Guide](scripts/proxmox/README.md) for details.

## New: macOS Native Installation

Install natively on macOS for maximum speed test performance (Docker Desktop limits network throughput for speed testing). See [macOS Installation Guide](docs/MACOS-INSTALLATION.md) for full instructions.

## New: Windows Installer

Download the MSI installer from [GitHub Releases](https://github.com/Ozark-Connect/NetworkOptimizer/releases) for one-click installation on Windows. Includes automatic service setup, bundled iperf3, OpenSpeedTest for browser-based speed tests, and runs at system startup.

## New: Client-Based LAN Speed Testing

Test LAN speeds from any device on your network - phones, tablets, laptops - without SSH access. Run browser-based speed tests powered by [OpenSpeedTest™](https://openspeedtest.com) or use iperf3 clients; results are automatically collected and displayed with device identification, network path visualization, and performance metrics. With HTTPS enabled, browser tests can collect location data (with permission) to build a Speed / Coverage Map showing real-world performance across your property or campus.

![Speed / Coverage Map](docs/images/speed-coverage-map.png)

---

You've set up VLANs, configured firewall rules, maybe even deployed a Pi-hole for DNS filtering. The UniFi controller gives you all this power, but it never actually tells you whether your configuration is any good. Are your firewall rules doing what you think they're doing? Is that IoT VLAN actually isolated, or did you miss something? When a device bypasses your DNS settings and phones home directly, would you even know?

Network Optimizer answers those questions. It connects to your UniFi controller, analyzes your configuration, and tells you what's working, what's broken, and what you should fix. No more guessing.

## Main Features

### Wi-Fi Optimizer

This feature brings comprehensive wireless network analysis to Network Optimizer. Twelve tabs cover everything from a 0-100 site health score and RF environment scanning to per-client signal history, roaming event timelines, and airtime fairness breakdowns. Channel recommendations use overlap-aware scoring against the full channel span at your AP’s configured width, pull available channels from the UniFi API so suggestions match what your country and hardware actually support, and filter to Preferred Scanning Channels on 6 GHz.

On the client side, you get a sortable, searchable table view with online/offline filtering, per-client signal and roaming history, and band-segmented Wi-Fi generation breakdowns showing exactly where your airtime is going. Environmental correlation heatmaps surface interference patterns by time of day and day of week, and every recommendation includes the specific UniFi Network UI navigation path to apply the change - no guessing which settings page to dig through.​​​​​​​​​​​​​​​​

### Security Auditing

The audit engine runs 63 security checks across five categories and scores your network 0-100. This isn't a checkbox audit that just confirms you have a firewall; it actually analyzes what your rules do and whether they're doing it correctly.

Firewall analysis catches the subtle stuff: rules that shadow each other, allow rules that subvert your deny rules, allow rules that punch holes through your network isolation. VLAN security checks whether your IoT devices and cameras are actually on the networks you intended (using UniFi fingerprints, MAC OUI lookup, and port naming patterns). DNS security validates your DoH configuration, checks for bypass routes (including DoT, DoQ, and HTTP/3 DoH bypass), and verifies that your WAN interface DNS settings match what you configured. Port security looks at MAC restrictions, port isolation, and whether you've left unused ports enabled. UPnP analysis flags enabled UPnP, exposed privileged ports, and static port forwards you may have forgotten about.

You get a score, a breakdown by severity (critical, recommended, informational), and specific recommendations for each issue. Dismiss false positives if your setup is intentional, export PDF reports for documentation, track your score over time.

### Adaptive SQM

If you're on cable, DSL, or cellular, you know bufferbloat. That lag spike when someone starts a download or joins a video call. SQM fixes it, but setting the bandwidth limits correctly is a guessing game; too high and SQM can't shape traffic effectively, too low and you're leaving speed on the table.

Network Optimizer handles this automatically. It supports dual-WAN with independent configuration per interface, connection profiles tuned for DOCSIS, fiber, wireless, Starlink, and cellular (each has different characteristics that matter). Scheduled speedtests adjust your rates based on actual measured performance. Latency monitoring backs off when congestion appears. One-click deployment pushes the configuration to your UDM or UCG gateway with persistence through reboots.

### LAN Speed Testing

Ever wonder if that new switch is actually delivering 10 gigabit speeds? Or whether the cable run to the shop is the bottleneck?

Network Optimizer runs iperf3 tests between your gateway and network devices, auto-discovers UniFi equipment from your controller, supports custom devices with per-device SSH credentials, auto indexes iperf3 results from tests initiated by other devices against the built in server (if enabled), and correlates results with hop count and infrastructure path, with detailed Wi-Fi stats and link speeds recorded along with UniFi firmware versions.

Test history lets you track performance over time with these relevant data in order to identify and characterize any changes to performance.

![LAN Speed Test](docs/images/lan-speed-test.png)

### Client Speed Testing

Test LAN speeds from any device without SSH access. Open a browser on your phone, tablet, or laptop and run a speed test; results are automatically recorded with device identification. For CLI users, the bundled iperf3 server accepts client connections and logs results. See [Client Speed Testing](docker/DEPLOYMENT.md#client-speed-testing-optional) in the deployment guide.

![Client Speed Test with Network Path](docs/images/client-speed-test-trace.png)

### Cellular Modem Monitoring

If you're running a U-LTE or U5G-Max for backup (or primary) connectivity, you can monitor signal quality from the dashboard: RSRP, RSRQ, SNR, cell tower info, and connection status. Supports multiple modems with easy navigation between them.

![Cellular Stats Demo](docs/images/cellular-stats.gif)

### UPnP Inspector

Ever wonder what ports your network is actually exposing to the internet? Your Xbox, Plex server, and smart home devices are all punching holes through your firewall via UPnP, and UniFi doesn't make it easy to see what's going on.

The UPnP Inspector puts it all in one place: every dynamic UPnP mapping and static port forward, grouped by device, with color-coded status so you can see at a glance what's active, what's idle, and what's about to expire. Add notes to remember what each mapping is for (because you will forget). Search and filter when you're hunting for that one port that's causing problems.

### Coming Soon

Cable modem stats (signal levels, uncorrectables, T3/T4 timeouts) for those of you fighting with your ISP about line quality.

## Requirements

- UniFi Console (aka Controller) - UDM, UCG, UDR, CloudKey, or self-hosted UniFi Network Server
- Network access to your UniFi Console API (HTTPS)

Most features work with just API access. SSH is only needed for speed testing and Adaptive SQM:

| Feature | SSH needed? |
|---------|------------|
| Security Audit | No |
| Config Optimizer | No, but Gateway SSH required for upcoming features |
| Wi-Fi Optimizer | No |
| Threat Intelligence | No |
| Alerts & Scheduling | No (schedules speed tests that may require SSH) |
| Client Speed Test | No |
| WAN Speed Test | No, but gateway-based requires Gateway SSH |
| LAN Speed Test | Yes - Gateway SSH and/or Device SSH |
| Adaptive SQM | Yes - Gateway SSH |

To enable SSH, see [SSH Configuration](docker/DEPLOYMENT.md#unifi-ssh-configuration) in the Deployment Guide. SSH must be configured via the UniFi web interface (not the mobile app).

## Installation

| Platform | Method | Guide |
|----------|--------|-------|
| Linux Server | Docker (recommended) | [Deployment Guide](docker/DEPLOYMENT.md#1-linux--docker-recommended) |
| Proxmox VE | LXC one-liner | [Proxmox Guide](scripts/proxmox/README.md) |
| Synology/QNAP/Unraid | Docker | [NAS Deployment](docker/DEPLOYMENT.md#3-nas-deployment-docker) |
| Home Assistant | Add-ons | [Home Assistant](docker/DEPLOYMENT.md#5-home-assistant) |
| Windows | Installer (recommended) | [Download from Releases](https://github.com/Ozark-Connect/NetworkOptimizer/releases) |
| macOS | Native (best performance) | [macOS Installation](docs/MACOS-INSTALLATION.md) |
| Linux | Native (no Docker) | [Linux Native](docker/NATIVE-DEPLOYMENT.md#linux-deployment) |

Docker Desktop on macOS and Windows limits network throughput for speed testing. For accurate multi-gigabit measurements, use native deployment.

### HTTPS Reverse Proxy

For HTTPS with automatic Let's Encrypt certificates, use [NetworkOptimizer-Proxy](https://github.com/Ozark-Connect/NetworkOptimizer-Proxy) - a Traefik setup that forces HTTP/1.1 for speed tests (HTTP/2 multiplexing skews results) while keeping HTTP/2 for the main app. Windows MSI users can enable Traefik as an optional feature during install. This also allows for simpler enablement of GPS-based tagging on your self-hosted Speed Test and Signal walk test data as browsers require HTTPS for location data to flow.

### Quick Start (Linux Docker)

**Option A: Pull Docker Image (Recommended)**

```bash
mkdir network-optimizer && cd network-optimizer
curl -o docker-compose.yml https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/docker/docker-compose.prod.yml
curl -O https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/docker/.env.example
cp .env.example .env
docker compose up -d

# Check logs for the auto-generated admin password
docker logs network-optimizer 2>&1 | grep -A5 "AUTO-GENERATED"
```

**Option B: Build from Source**

```bash
git clone https://github.com/Ozark-Connect/NetworkOptimizer.git
cd NetworkOptimizer/docker
cp .env.example .env
docker compose build
docker compose up -d

# Check logs for the auto-generated admin password
docker logs network-optimizer 2>&1 | grep -A5 "AUTO-GENERATED"
```

Open http://localhost:8042

### Quick Start (Proxmox)

```bash
bash -c "$(wget -qLO - https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/proxmox/install.sh)"
```

### First Run

1. Go to Settings and enter your UniFi controller URL
2. Create a **Local Access Only** account on your controller (Ubiquiti SSO won't work):
   - Quick: Super Admin role
   - Restricted: Network View Only, Protect View Only, User Management None
   - See the in-app setup guide or [detailed instructions](docker/DEPLOYMENT.md#unifi-account)
3. Click Connect to authenticate
4. Navigate to Audit to run your first security scan

## Project Structure

```
src/
├── NetworkOptimizer.Web         # Blazor web UI
├── NetworkOptimizer.Alerts      # Alerts & Scheduling engine
├── NetworkOptimizer.Audit       # Security Audit
├── NetworkOptimizer.Core        # Shared helpers and utilities
├── NetworkOptimizer.Diagnostics # Config Optimizer
├── NetworkOptimizer.Monitoring  # SNMP/SSH polling
├── NetworkOptimizer.Reports     # PDF/Markdown report generation
├── NetworkOptimizer.Sqm         # Adaptive SQM
├── NetworkOptimizer.Storage     # SQLite database
├── NetworkOptimizer.Threats     # Threat Intelligence
├── NetworkOptimizer.UniFi       # UniFi API client
├── NetworkOptimizer.WiFi        # Wi-Fi Optimizer
├── cfspeedtest/                 # WAN Speed Test (binary for gateway)
└── OpenSpeedTest/               # Client Speed Test
```

## Tech Stack

.NET 10, Blazor Server, SQLite, iperf3, SSH.NET, QuestPDF, OpenSpeedTest™, Go (WAN speed test binary)

## Password Reset

If you forget the admin password, use the reset script for your platform:

**Docker / macOS / Linux:**
```bash
curl -fsSL https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/reset-password.sh | bash
```

**Windows (PowerShell as Administrator):**
```powershell
irm https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/reset-password.ps1 -OutFile reset-password.ps1
.\reset-password.ps1
```

The script stops the service, clears the password, restarts, and shows you the new temporary password.

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

---

<sub>Network Optimizer for UniFi is an independent project by Ozark Connect and is not affiliated with, endorsed by, or sponsored by Ubiquiti, Inc. Ubiquiti, UniFi, UDM, and Cloud Key are trademarks or registered trademarks of Ubiquiti, Inc. All other trademarks are the property of their respective owners.</sub>
