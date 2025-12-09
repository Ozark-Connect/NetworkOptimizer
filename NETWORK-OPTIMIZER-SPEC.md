# Ozark Connect Network Optimizer for UniFi

## Product Specification & Architecture Document

---

## Executive Summary

**Product Name:** Ozark Connect Network Optimizer for UniFi
**Short Name:** Network Optimizer
**Company:** Ozark Connect
**Target Market:** UniFi prosumers, home lab enthusiasts, small MSPs
**Price Point:** $30-50 (Standard), $10-15/site/year (MSP)
**Distribution:** Docker container with web UI (cross-platform)

### Value Proposition

Ubiquiti gives you data and configuration options but no intelligence about whether your config is *good*. Network Optimizer fills that gap with:

1. **Dynamic SQM Management** - Adaptive bandwidth optimization that learns your ISP patterns (unique differentiator)
2. **Security & Configuration Auditing** - Expert-level analysis of firewall rules, VLANs, port security
3. **Comprehensive Monitoring** - Time-series metrics with distributed agents
4. **Actionable Insights** - Not "AI" - just intelligent automation that levels up your network

### Tagline Options

- *"Level up your UniFi network"*
- *"Expert-level network intelligence"*
- *"The smarts your UniFi network deserves"*

---

## Target Audience

| Segment | Description | Key Needs |
|---------|-------------|-----------|
| **UniFi Prosumers** | Power users with UCG/UDM at home | SQM optimization, security hardening, visibility |
| **Home Labbers** | Tech enthusiasts running Proxmox/TrueNAS | Docker deployment, extensive monitoring, API access |
| **Small MSPs** | Managing 5-50 client UniFi networks | Multi-site management, white-label reports, bulk auditing |

### Platform Expectations

Many UniFi users are NOT Windows users:
- macOS power users
- Linux home-labbers (Proxmox, TrueNAS, bare metal)
- Docker-first infrastructure people
- NAS users (Synology, Unraid, QNAP)

**Primary deployment: Docker container with web UI**

---

## Core Features

### 1. Dynamic SQM Manager (MVP - Unique Differentiator)

The killer feature. No competitor does this.

#### What It Does

Adaptive bandwidth management that prevents bufferbloat by:
- Running scheduled speedtests to measure actual bandwidth
- Monitoring latency continuously via ping
- Maintaining 168-hour (7-day) baseline tables by day/hour
- Dynamically adjusting tc/HTB classes based on measured vs. baseline
- Blending algorithms: 60/40 or 80/20 baseline/measured based on variance

#### Why It Matters

- **Prevents bufferbloat** - Never saturates the line (always 90-95% of max)
- **Handles ISP variation** - Baseline smooths out temporary speed drops
- **Optimizes for latency** - Ping script detects congestion faster than speedtest
- **Dual-WAN support** - Separate tuning for different connections (cable vs. Starlink)

#### Supported Devices

- UCG-Ultra, UCG-Max, UCG-Fiber
- UDM, UDM Pro, UDM SE, UDM Pro Max

All use `/data/on_boot.d/` via udm-boot for persistence.

#### User Experience

```
┌─────────────────────────────────────────────────────────────────────────────┐
│  ADAPTIVE SQM MANAGER                                                       │
│                                                                             │
│  SETUP WIZARD                                                               │
│  1. Detect WAN interface(s) and current speed tier                          │
│  2. Run initial speedtest to establish baseline                             │
│  3. Configure monitoring schedule (default: 6AM/6PM speedtest, 5min ping)   │
│  4. Deploy scripts to UCG/UDM via SSH                                       │
│  5. Optional: Import historical baseline from previous data                 │
│                                                                             │
│  LEARNING MODE (Days 1-7)                                                   │
│  ├── Run speedtest every 2 hours (12 samples/day)                           │
│  ├── Build 168-hour baseline table                                          │
│  ├── Calculate per-hour mean, stddev, min, max                              │
│  └── Dashboard shows: "Learning: 45% complete (76/168 hours)"               │
│                                                                             │
│  ACTIVE OPTIMIZATION (Day 8+)                                               │
│  ├── Use learned baselines for blending                                     │
│  ├── Continue collecting data to refine baselines                           │
│  └── Alert if sustained deviation from baseline (ISP change?)               │
└─────────────────────────────────────────────────────────────────────────────┘
```

#### Deployment Options

| Mode | Description | User Type |
|------|-------------|-----------|
| **Generate Only** | Output shell scripts + instructions | Security-conscious, manual deployers |
| **SSH Deploy** | One-click deployment via SSH | Convenience seekers |
| **SSH + Monitor** | Deploy + continuously verify scripts running | Full automation |

---

### 2. Security & Configuration Audit Engine (MVP)

Port and extend existing Python audit code to C#.

#### Audit Categories

**Firewall Rules:**
- Shadowed rules (never hit due to earlier rules)
- Overly permissive rules (any/any patterns)
- Orphaned rules referencing deleted groups/networks
- Missing inter-VLAN isolation where expected

**VLAN Security:**
- Devices on wrong VLANs (IoT on corporate, etc.)
- DNS/gateway leakage across VLANs
- Missing VLAN isolation for IoT/guest networks

**Port Security:**
- Missing MAC restrictions on access ports
- Unused ports not disabled
- Cameras not on Security VLAN
- Port isolation not enabled where appropriate

**DNS Leak Detection:**
- Devices bypassing designated DNS servers
- Missing firewall rules blocking outbound 53/853

#### Scoring System

```
Security Posture Rating:
├── 0 critical issues     → EXCELLENT ✓
├── 1-2 critical issues   → GOOD (needs attention)
├── 3-5 critical issues   → FAIR ⚠
└── 6+ critical issues    → NEEDS WORK ✗

Score: 0-100 based on weighted issue counts
Trend tracking over time
```

#### Issue Severity Levels

| Severity | Examples |
|----------|----------|
| **Critical** | IoT device on corporate VLAN, any/any firewall rule |
| **Warning** | Missing MAC restriction, unused port enabled |
| **Info** | Naming suggestions, optimization opportunities |

---

### 3. Comprehensive Monitoring Suite (MVP)

Leverages existing SeaTurtleMonitor patterns with InfluxDB + Grafana.

#### Metrics Collected

**From UniFi API (direct polling):**
- Device status and health
- Client connection quality
- Wi-Fi channel utilization
- Firewall rule hit counts

**From SNMP (via poller):**
- Switch port utilization and errors
- AP metrics
- Interface counters (64-bit for 10G+)
- CPU, memory, temperature

**From Distributed Agents:**
- SQM statistics (rate, baseline, latency, adjustments)
- Speedtest results
- System metrics (Linux hosts)
- Docker container stats

#### InfluxDB Schema

```
# SQM Stats
sqm_stats,device=udm-pro,interface=eth2 rate=265.5,baseline=270.0,latency=18.2,adjustment="none"

# Speedtest Results
speedtest,device=udm-pro,interface=eth2,server="Cox Phoenix" download=285.4,upload=35.2,latency=12.5

# Device Metrics
device_metrics,device=usw-enterprise-24-poe,type=switch cpu=12.5,memory_used=45.2,uptime=8640000

# Interface Metrics
interface_metrics,device=usw-enterprise-24-poe,port=1,port_name="Uplink" in_octets=123456789,out_octets=987654321

# Client Metrics
client_metrics,device=u7-pro,client_mac=aa:bb:cc:dd:ee:ff signal=-45,tx_rate=1200,channel=149
```

#### Pre-Built Grafana Dashboards

| Dashboard | Purpose |
|-----------|---------|
| **Network Overview** | High-level health: all devices, alerts, SQM status |
| **SQM Performance** | Rate over time, latency, baseline comparison, speedtest history |
| **Switch Deep-Dive** | Per-port utilization, PoE, errors, top talkers |
| **AP Performance** | Client counts, channel utilization, roaming events |
| **Client Analytics** | Signal quality, connection duration, band distribution |
| **Security Posture** | Audit score trend, issue count over time |

---

### 4. Report Generation (MVP)

Professional reports for documentation and client delivery.

#### Formats

| Format | Audience | Use Case |
|--------|----------|----------|
| **PDF** | Clients, management | Polished, branded deliverable |
| **Markdown** | Technical users | Wiki, ticketing, version control |
| **HTML** | Email delivery | Inline summary (Phase 2) |
| **JSON** | Automation | SIEM, ticketing APIs (Phase 2) |

#### Report Sections

1. Executive Summary with security posture rating
2. Network topology overview
3. Critical issues requiring immediate action
4. Recommended improvements
5. Per-device detailed analysis
6. Historical trend comparison (if available)

#### Branding Options

| License Tier | Branding |
|--------------|----------|
| **Standard** | "Generated by Network Optimizer" |
| **MSP** | Custom logo, company name, remove product branding |

---

### 5. Distributed Agent System (MVP)

Deploy lightweight monitoring agents to collect metrics from locations the central hub can't reach.

#### Agent Types

| Agent | Runs On | Deployment | Collects |
|-------|---------|------------|----------|
| **UDM/UCG Agent** | UniFi gateway | SSH → `/data/on_boot.d/` | SQM stats, speedtest, latency, tc class stats |
| **Linux Agent** | Any Linux box | SSH → systemd service | System metrics, Docker stats, custom checks |
| **SNMP Poller** | Central hub OR distributed | Part of hub OR separate | Switch/AP metrics via SNMP |

#### Deployment UX

```
┌─────────────────────────────────────────────────────────────────────────────┐
│  AGENT DEPLOYMENT WIZARD                                                    │
│                                                                             │
│  Step 1: Select Agent Type                                                  │
│  ┌─────────────────────────────────────────────────────────────────────┐   │
│  │ ○ UDM/UCG Gateway Agent (SQM, speedtest, latency monitoring)       │   │
│  │ ○ Linux System Agent (CPU, memory, disk, Docker, custom)           │   │
│  │ ○ SNMP Poller (for switches, APs, other SNMP devices)              │   │
│  └─────────────────────────────────────────────────────────────────────┘   │
│                                                                             │
│  Step 2: Connection Details                                                 │
│  ┌─────────────────────────────────────────────────────────────────────┐   │
│  │ Hostname/IP: [192.168.1.1          ]                               │   │
│  │ SSH User:    [root                 ]                               │   │
│  │ Auth Method: ○ Password  ● SSH Key                                 │   │
│  │ [Test Connection]  → ✓ Connected successfully                      │   │
│  └─────────────────────────────────────────────────────────────────────┘   │
│                                                                             │
│  Step 3: Configure & Deploy                                                 │
│  ┌─────────────────────────────────────────────────────────────────────┐   │
│  │ [Deploy via SSH]     [Generate Scripts for Manual Install]         │   │
│  └─────────────────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────────────┘
```

#### Manual Deployment Option

For users who prefer not to give SSH access:

```bash
# Download generated bundle
# Upload to device
scp agent-bundle.tar.gz root@192.168.1.1:/data/
ssh root@192.168.1.1 "cd /data && tar xzf agent-bundle.tar.gz && /data/network-optimizer/install.sh"
```

---

## Architecture

### System Overview

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│                     Network Optimizer - Full Architecture                        │
├─────────────────────────────────────────────────────────────────────────────────┤
│                                                                                  │
│  ┌─────────────────────────────────────────────────────────────────────────┐    │
│  │                         CENTRAL HUB (Docker)                            │    │
│  │  User deploys on: NAS, Proxmox VM, Raspberry Pi, Cloud VPS, etc.       │    │
│  │                                                                         │    │
│  │  ┌──────────────┐  ┌──────────────┐  ┌──────────────────────────────┐  │    │
│  │  │ Blazor Server│  │ InfluxDB     │  │ Grafana                      │  │    │
│  │  │ Web UI       │◄─┤ Time-Series  │◄─┤ Dashboards                   │  │    │
│  │  │ :8080        │  │ :8086        │  │ :3000                        │  │    │
│  │  └──────────────┘  └──────────────┘  └──────────────────────────────┘  │    │
│  │         │                 ▲                                            │    │
│  │         │                 │ Write metrics                              │    │
│  │         ▼                 │                                            │    │
│  │  ┌─────────────────────────────────────────────────────────────────┐  │    │
│  │  │                    CORE ANALYSIS ENGINE                         │  │    │
│  │  │  ┌─────────────┐ ┌─────────────┐ ┌─────────────┐ ┌───────────┐ │  │    │
│  │  │  │Config Audit │ │Security     │ │Dynamic SQM  │ │Monitoring │ │  │    │
│  │  │  │Engine       │ │Audit Engine │ │Manager      │ │Aggregator │ │  │    │
│  │  │  └─────────────┘ └─────────────┘ └─────────────┘ └───────────┘ │  │    │
│  │  └─────────────────────────────────────────────────────────────────┘  │    │
│  │         │                                              ▲               │    │
│  │         │ UniFi API                                    │ Agent Data    │    │
│  │         ▼                                              │               │    │
│  │  ┌──────────────┐                           ┌──────────────────────┐  │    │
│  │  │ UniFi        │                           │ Agent Manager        │  │    │
│  │  │ Controller   │                           │ - Deploy via SSH     │  │    │
│  │  │ Integration  │                           │ - Health monitoring  │  │    │
│  │  └──────────────┘                           └──────────────────────┘  │    │
│  └─────────────────────────────────────────────────────────────────────────┘    │
│                                                                                  │
├──────────────────────────────────────────────────────────────────────────────────┤
│                                                                                  │
│  ┌─────────────────────────────────────────────────────────────────────────┐    │
│  │                    DISTRIBUTED MONITORING AGENTS                        │    │
│  │                                                                         │    │
│  │  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────────────┐ │    │
│  │  │ UDM/UCG Agent   │  │ Linux Agent     │  │ SNMP Poller Agent      │ │    │
│  │  │ (on-boot script)│  │ (systemd svc)   │  │ (central or distributed)│ │    │
│  │  │                 │  │                 │  │                         │ │    │
│  │  │ • SQM metrics   │  │ • System stats  │  │ • Switch port stats    │ │    │
│  │  │ • Speedtest     │  │ • Docker stats  │  │ • AP metrics           │ │    │
│  │  │ • Latency       │  │ • Custom checks │  │ • Interface counters   │ │    │
│  │  └────────┬────────┘  └────────┬────────┘  └────────────┬────────────┘ │    │
│  │           │                    │                        │              │    │
│  │           └────────────────────┴────────────────────────┘              │    │
│  │                                │                                       │    │
│  │                    Push metrics to InfluxDB                            │    │
│  └─────────────────────────────────────────────────────────────────────────┘    │
│                                                                                  │
└──────────────────────────────────────────────────────────────────────────────────┘
```

### Data Flow

```
┌──────────────┐     ┌──────────────┐     ┌──────────────┐     ┌──────────────┐
│ UDM/UCG      │     │ Linux        │     │ SNMP Poller  │     │ UniFi API    │
│ Agent        │     │ Agent        │     │              │     │ (direct)     │
└──────┬───────┘     └──────┬───────┘     └──────┬───────┘     └──────┬───────┘
       │                    │                    │                    │
       │ HTTP POST          │ HTTP POST          │ Direct write       │ Poll
       │ /api/metrics       │ /api/metrics       │ to InfluxDB        │
       │                    │                    │                    │
       ▼                    ▼                    ▼                    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                         CENTRAL HUB                                          │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │                    Metrics Ingestion API                              │  │
│  │  • Validates agent tokens                                             │  │
│  │  • Normalizes metric names                                            │  │
│  │  • Batches writes to InfluxDB                                         │  │
│  │  • Triggers alerts on thresholds                                      │  │
│  └───────────────────────────────────────────────────────────────────────┘  │
│                                    │                                        │
│                                    ▼                                        │
│                              InfluxDB                                       │
│                                    │                                        │
│                    ┌───────────────┼───────────────┐                       │
│                    ▼               ▼               ▼                       │
│            Blazor UI          Grafana        Alert Engine                  │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## Technology Stack

| Layer | Technology | Rationale |
|-------|-----------|-----------|
| **Core Engine** | .NET 9 | Known quantity, SeaTurtleMonitor patterns ready |
| **Web UI** | Blazor Server | Single codebase, real-time updates, no JS build chain |
| **Time-Series DB** | InfluxDB 2.x | Already built in SeaTurtleMonitor, production-proven |
| **Dashboards** | Grafana | Already built, shipped pre-configured |
| **Reports** | QuestPDF | Pure .NET, no Python dependency |
| **Local Storage** | SQLite + EF Core | Config, audit results, license - simple, file-based |
| **UniFi API** | HttpClient + cookie auth | Simulate browser session to web UI backend |
| **SNMP** | SharpSnmpLib | Already using in SeaTurtleMonitor |
| **SSH** | SSH.NET | Agent deployment to UCG/UDM |
| **Containerization** | Docker + Docker Compose | Cross-platform deployment |

### Reusable Components from Existing Projects

| Source Project | Component | Reuse |
|----------------|-----------|-------|
| **SeaTurtleMonitor** | InfluxDbStorage | Direct lift with minor modifications |
| **SeaTurtleMonitor** | SnmpPoller | Direct lift |
| **SeaTurtleMonitor** | IDevicePoller<T>, IDeviceDiscovery<T> | Interface patterns |
| **SeaTurtleMonitor** | Batching/buffering for writes | Direct lift |
| **OzarkConnect-UniFiPortAudit** | Audit logic, scoring | Port to C# |
| **OzarkConnect-UniFiPortAudit** | Report structure | Port to QuestPDF |
| **UniFiCloudGatewayCustomization** | SQM scripts | Template and parameterize |
| **UniFiCloudGatewayCustomization** | Baseline algorithms | Port to C# |
| **SeaTurtleGamingBuddy** | UniFi API patterns | Reference for auth flow |

---

## Project Structure

```
NetworkOptimizer/
├── src/
│   ├── NetworkOptimizer.Core/              # Shared models, interfaces, enums
│   │   ├── Models/
│   │   │   ├── UniFiDevice.cs
│   │   │   ├── AuditResult.cs
│   │   │   ├── SqmConfiguration.cs
│   │   │   └── AgentStatus.cs
│   │   ├── Interfaces/
│   │   │   ├── IUniFiApiClient.cs
│   │   │   ├── IAuditEngine.cs
│   │   │   ├── IMetricsStorage.cs
│   │   │   └── IAgentDeployer.cs
│   │   └── Enums/
│   │
│   ├── NetworkOptimizer.UniFi/             # UniFi API integration
│   │   ├── UniFiApiClient.cs
│   │   ├── UniFiDiscovery.cs
│   │   └── Models/
│   │
│   ├── NetworkOptimizer.Audit/             # Audit engines
│   │   ├── ConfigAuditEngine.cs
│   │   ├── SecurityAuditEngine.cs
│   │   ├── FirewallRuleAnalyzer.cs
│   │   ├── VlanAnalyzer.cs
│   │   └── Rules/
│   │
│   ├── NetworkOptimizer.Sqm/               # Dynamic SQM management
│   │   ├── SqmManager.cs
│   │   ├── BaselineCalculator.cs
│   │   ├── SpeedtestIntegration.cs
│   │   └── ScriptGenerator.cs
│   │
│   ├── NetworkOptimizer.Monitoring/        # Metrics collection
│   │   ├── SnmpPoller.cs
│   │   ├── MetricsAggregator.cs
│   │   └── AlertEngine.cs
│   │
│   ├── NetworkOptimizer.Storage/           # Data persistence
│   │   ├── InfluxDbStorage.cs
│   │   ├── SqliteRepository.cs
│   │   └── Migrations/
│   │
│   ├── NetworkOptimizer.Agents/            # Agent deployment & management
│   │   ├── AgentDeployer.cs
│   │   ├── AgentHealthMonitor.cs
│   │   ├── Templates/
│   │   │   ├── udm-agent.sh.template
│   │   │   ├── linux-agent.sh.template
│   │   │   └── install.sh.template
│   │   └── ScriptRenderer.cs
│   │
│   ├── NetworkOptimizer.Reports/           # Report generation
│   │   ├── PdfReportGenerator.cs
│   │   ├── MarkdownReportGenerator.cs
│   │   └── Templates/
│   │
│   ├── NetworkOptimizer.Licensing/         # License validation
│   │   ├── LicenseValidator.cs
│   │   ├── ControllerFingerprint.cs
│   │   └── GracePeriodManager.cs
│   │
│   ├── NetworkOptimizer.Web/               # Blazor Server app
│   │   ├── Program.cs
│   │   ├── Components/
│   │   │   ├── Layout/
│   │   │   ├── Pages/
│   │   │   │   ├── Dashboard.razor
│   │   │   │   ├── Audit.razor
│   │   │   │   ├── Sqm.razor
│   │   │   │   ├── Agents.razor
│   │   │   │   ├── Reports.razor
│   │   │   │   └── Settings.razor
│   │   │   └── Shared/
│   │   ├── Services/
│   │   └── wwwroot/
│   │
│   └── NetworkOptimizer.Api/               # Metrics ingestion API
│       ├── Controllers/
│       │   └── MetricsController.cs
│       └── Program.cs
│
├── agents/                                  # Standalone agent scripts
│   ├── udm/
│   │   ├── 50-network-optimizer-agent.sh
│   │   ├── sqm-manager.sh
│   │   ├── metrics-collector.sh
│   │   └── install.sh
│   └── linux/
│       ├── network-optimizer-agent.service
│       ├── agent.sh
│       └── install.sh
│
├── docker/
│   ├── Dockerfile
│   ├── docker-compose.yml
│   └── grafana/
│       ├── provisioning/
│       │   ├── datasources/
│       │   └── dashboards/
│       └── dashboards/
│           ├── network-overview.json
│           ├── sqm-performance.json
│           ├── switch-deep-dive.json
│           ├── ap-performance.json
│           ├── client-analytics.json
│           └── security-posture.json
│
├── tests/
│   ├── NetworkOptimizer.Core.Tests/
│   ├── NetworkOptimizer.Audit.Tests/
│   └── NetworkOptimizer.Sqm.Tests/
│
├── docs/
│   ├── setup-guide.md
│   ├── agent-deployment.md
│   └── api-reference.md
│
├── NetworkOptimizer.sln
├── README.md
├── LICENSE
└── .github/
    └── workflows/
        ├── build.yml
        └── release.yml
```

---

## Docker Compose

```yaml
version: '3.8'

services:
  optimizer:
    image: ghcr.io/ozark-connect/network-optimizer:latest
    container_name: network-optimizer
    ports:
      - "8080:8080"      # Blazor web UI
      - "8081:8081"      # Metrics ingestion API (for agents)
    volumes:
      - ./data:/app/data              # SQLite, configs, license
      - ./ssh-keys:/app/ssh-keys:ro   # Optional: SSH keys for agent deployment
    environment:
      - INFLUXDB_URL=http://influxdb:8086
      - INFLUXDB_TOKEN=${INFLUXDB_TOKEN}
      - INFLUXDB_ORG=network-optimizer
      - INFLUXDB_BUCKET=network_optimizer
    depends_on:
      - influxdb

  influxdb:
    image: influxdb:2.7
    container_name: network-optimizer-influxdb
    ports:
      - "8086:8086"
    volumes:
      - influxdb-data:/var/lib/influxdb2
      - influxdb-config:/etc/influxdb2
    environment:
      - DOCKER_INFLUXDB_INIT_MODE=setup
      - DOCKER_INFLUXDB_INIT_USERNAME=admin
      - DOCKER_INFLUXDB_INIT_PASSWORD=${INFLUXDB_PASSWORD}
      - DOCKER_INFLUXDB_INIT_ORG=network-optimizer
      - DOCKER_INFLUXDB_INIT_BUCKET=network_optimizer
      - DOCKER_INFLUXDB_INIT_RETENTION=30d

  grafana:
    image: grafana/grafana:latest
    container_name: network-optimizer-grafana
    ports:
      - "3000:3000"
    volumes:
      - grafana-data:/var/lib/grafana
      - ./grafana/provisioning:/etc/grafana/provisioning:ro
      - ./grafana/dashboards:/var/lib/grafana/dashboards:ro
    environment:
      - GF_SECURITY_ADMIN_PASSWORD=${GRAFANA_PASSWORD}
      - GF_USERS_ALLOW_SIGN_UP=false
      - GF_AUTH_ANONYMOUS_ENABLED=true
      - GF_AUTH_ANONYMOUS_ORG_ROLE=Viewer
    depends_on:
      - influxdb

volumes:
  influxdb-data:
  influxdb-config:
  grafana-data:
```

---

## Licensing Model

### Controller-Bound with Offline Grace Period

```
┌─────────────────────────────────────────────────────────────────────────────┐
│  LICENSING FLOW                                                             │
│                                                                             │
│  1. User purchases license key from website                                 │
│  2. First run: Enter license key + connect to UniFi controller             │
│  3. App captures controller's unique ID (from /api/s/default/stat/sysinfo) │
│  4. One-time online activation binds key → controller ID                   │
│  5. License stored locally with controller fingerprint                     │
│  6. Subsequent runs: Validate controller ID matches, no internet needed    │
│  7. Grace period: 30 days if controller ID changes (hardware swap)         │
│  8. Re-activation: User can request via support portal                     │
└─────────────────────────────────────────────────────────────────────────────┘
```

### License Tiers

| Tier | Price | Features |
|------|-------|----------|
| **Standard** | $30-50 one-time | Single controller, full features, branded reports |
| **MSP** | $10-15/site/year | Multi-site, white-label reports, bulk operations, priority support |

---

## MVP vs Phase 2

### MVP (Ship First)

| Feature | Priority | Status |
|---------|----------|--------|
| Docker deployment with Blazor UI | P0 | Required |
| Dynamic SQM Manager | P0 | Unique differentiator |
| Security Audit Engine | P0 | Core value prop |
| Configuration Audit | P1 | High value |
| InfluxDB + Grafana monitoring | P1 | Already built |
| PDF/Markdown reports | P1 | Already built (port) |
| Agent deployment (SSH + manual) | P1 | Key UX |
| Basic licensing (controller-bound) | P2 | Can ship with simple key initially |

### Phase 2 (After Validation)

| Feature | Notes |
|---------|-------|
| Wi-Fi optimization analysis | Channel, band steering, minimum RSSI |
| One-click remediation | Apply fixes with confirmation |
| Multi-site MSP dashboard | Aggregate view across clients |
| White-label reports | Custom branding for MSPs |
| Native Windows/macOS apps | .NET MAUI Blazor Hybrid |
| Advanced alerting | Email, webhook, Pushover, ntfy |
| Config backup/rollback | Before any changes |
| Historical baseline import | CSV, Speedtest CLI history |

---

## Success Criteria for MVP

1. ✅ User can connect to UniFi controller with local-only credentials
2. ✅ Dynamic SQM deploys to UCG/UDM and actively optimizes bandwidth
3. ✅ Application performs comprehensive security/config audit
4. ✅ Results displayed in clean web dashboard
5. ✅ PDF/Markdown report exportable
6. ✅ Monitoring with Grafana dashboards
7. ✅ Works cross-platform via Docker
8. ✅ Simple license key validation

---

## Legal Disclaimers

**Trademark Notice:**
UniFi is a trademark of Ubiquiti Inc. Ozark Connect is not affiliated with or endorsed by Ubiquiti Inc. "Network Optimizer for UniFi" describes compatibility with UniFi products.

---

## Document History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2024-XX-XX | [Author] | Initial specification |
