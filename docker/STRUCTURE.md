# Docker Infrastructure Structure

Complete overview of the Network Optimizer Docker infrastructure.

## Directory Structure

```
docker/
├── Dockerfile                          # Multi-stage .NET 9 build
├── docker-compose.yml                  # Production stack definition
├── docker-compose.override.yml.example # Development overrides template
├── entrypoint.sh                       # Container startup script
├── .env.example                        # Environment template
├── .dockerignore                       # Build optimization
│
├── README.md                           # Quick start guide
├── DEPLOYMENT.md                       # Production deployment guide
├── STRUCTURE.md                        # This file
│
├── start.sh                            # Quick start script
├── stop.sh                             # Stop services script
├── reset.sh                            # Reset all data script
├── backup.sh                           # Backup data script
├── restore.sh                          # Restore from backup script
│
├── grafana/
│   ├── provisioning/
│   │   ├── datasources/
│   │   │   └── influxdb.yml           # Auto-configure InfluxDB datasource
│   │   └── dashboards/
│   │       └── dashboards.yml         # Dashboard provisioning config
│   └── dashboards/
│       ├── network-overview.json      # Main health dashboard
│       ├── sqm-performance.json       # SQM metrics dashboard
│       ├── switch-deep-dive.json      # Per-port analysis dashboard
│       └── security-posture.json      # Audit score dashboard
│
├── data/                               # (Created at runtime)
│   ├── network-optimizer.db           # SQLite database
│   ├── configs/                       # Application configs
│   └── license/                       # License files
│
├── logs/                               # (Created at runtime)
│   └── *.log                          # Application logs
│
├── ssh-keys/                           # (Optional, user-created)
│   ├── id_rsa                         # SSH private key
│   └── id_rsa.pub                     # SSH public key
│
└── backups/                            # (Created by backup.sh)
    └── network-optimizer-backup-*.tar.gz
```

## Files Description

### Core Docker Files

#### Dockerfile
Multi-stage build for .NET 9 application:
- **Stage 1 (build)**: .NET SDK 9.0, compile application
- **Stage 2 (runtime)**: ASP.NET runtime 9.0, minimal image
- Includes health checks, proper user setup, volume mounts
- Optimized for production use

#### docker-compose.yml
Main orchestration file defining:
- **network-optimizer** service: Web UI + API
- **influxdb** service: Time-series database
- **grafana** service: Dashboards
- Networking, volumes, dependencies, health checks

#### entrypoint.sh
Container startup script:
- Starts Web UI on port 8080
- Starts Metrics API on port 8081
- Manages both processes

### Configuration Files

#### .env.example
Template with all configuration options:
- Port mappings
- Database credentials
- Security tokens
- Timezone settings
- Feature flags

Copy to `.env` and customize for your deployment.

#### .dockerignore
Excludes unnecessary files from Docker build context:
- Source control files (.git)
- Documentation
- Build artifacts
- IDE files
- Test files
- Improves build speed and reduces image size

### Grafana Configuration

#### grafana/provisioning/datasources/influxdb.yml
Auto-configures InfluxDB connection:
- Datasource name: "InfluxDB-NetworkOptimizer"
- Uses Flux query language
- Reads credentials from environment
- Set as default datasource

#### grafana/provisioning/dashboards/dashboards.yml
Dashboard auto-provisioning:
- Loads all JSON files from dashboards directory
- Creates "Network Optimizer" folder
- Allows UI updates (not read-only)
- 30-second refresh interval

### Grafana Dashboards

#### network-overview.json
Main monitoring dashboard:
- **Stats**: Device count, SQM status, security score, critical issues
- **Graphs**: Bandwidth trends, latency, device status table
- **Time Range**: Last 6 hours default
- **Refresh**: 30 seconds

**Panels:**
1. Header text
2. Total devices (stat)
3. SQM status (stat with color coding)
4. Security score (gauge 0-100)
5. Critical issues count (stat)
6. WAN bandwidth graph (rate vs baseline)
7. Device status table
8. WAN latency graph

#### sqm-performance.json
Adaptive bandwidth management:
- **Stats**: Current rate, baseline, latency gauge, last adjustment
- **Graphs**: Rate vs baseline trends, latency over time, adjustment frequency, speedtest history
- **Tables**: Recent speedtest results with details
- **Variables**: Device selector, interface selector
- **Time Range**: Last 24 hours default

**Panels:**
1. Header
2. Current rate (Mbps)
3. Baseline rate (Mbps)
4. Current latency (gauge with thresholds)
5. Last adjustment (status)
6. Rate vs baseline graph (dual line)
7. Latency over time
8. Adjustment frequency (bar chart)
9. Speedtest scatter plot
10. Speedtest results table

#### switch-deep-dive.json
Per-port network analysis:
- **Stats**: Active ports, CPU, memory, total PoE
- **Tables**: Port status with utilization and PoE
- **Graphs**: Per-port RX/TX rates, error rates, PoE trends
- **Variables**: Switch selector, port selector
- **Time Range**: Last 6 hours default

**Panels:**
1. Header
2. Active ports count
3. Switch CPU gauge
4. Switch memory gauge
5. Total PoE power
6. Port status table (all ports)
7. Selected port RX rate
8. Selected port TX rate
9. Selected port errors
10. PoE consumption trends

#### security-posture.json
Security audit visualization:
- **Gauges**: Overall security score (0-100 with thresholds)
- **Stats**: Critical/warning/info counts, last audit time
- **Graphs**: Score trend, issues by severity, issues by category
- **Tables**: Current issues detail, audit history
- **Pie Chart**: Issue distribution
- **Time Range**: Last 7 days default
- **Refresh**: 1 minute

**Panels:**
1. Header
2. Security score gauge (large)
3. Critical issues count
4. Warnings count
5. Info items count
6. Last audit timestamp
7. Score trend line
8. Issues by severity (stacked graph)
9. Issues by category (bar gauge)
10. Current issues table
11. Issue distribution pie chart
12. Audit history table

### Management Scripts

#### start.sh
Quick start script:
- Checks prerequisites (Docker, Docker Compose)
- Creates `.env` with secure random passwords if missing
- Creates required directories
- Pulls Docker images
- Starts all services
- Displays access information

**Usage:**
```bash
./start.sh
```

#### stop.sh
Gracefully stops all services:
- Stops containers
- Preserves data volumes

**Usage:**
```bash
./stop.sh
```

#### reset.sh
Complete data wipe:
- Stops containers
- Removes all volumes
- Deletes local data
- Deletes logs
- **WARNING: Irreversible!**

**Usage:**
```bash
./reset.sh
```

Requires double confirmation.

#### backup.sh
Creates backup archive:
- Backs up local data directory
- Backs up `.env` file
- Backs up InfluxDB (if running)
- Backs up Grafana data (if running)
- Creates compressed archive
- Cleans up old backups (keeps last 7)

**Usage:**
```bash
./backup.sh

# Custom backup location
BACKUP_DIR=/mnt/backups ./backup.sh
```

**Output:**
```
backups/network-optimizer-backup-YYYYMMDD-HHMMSS.tar.gz
```

#### restore.sh
Restores from backup:
- Stops services
- Extracts backup
- Restores data, .env, InfluxDB, Grafana
- Restarts services

**Usage:**
```bash
./restore.sh backups/network-optimizer-backup-20240101-120000.tar.gz
```

## Data Volumes

### Named Volumes (Docker-managed)

```yaml
volumes:
  influxdb-data:      # InfluxDB time-series data
  influxdb-config:    # InfluxDB configuration
  grafana-data:       # Grafana dashboards and settings
```

These persist across container restarts and recreates.

**Location:**
- Linux: `/var/lib/docker/volumes/`
- macOS: `~/Library/Containers/com.docker.docker/Data/`
- Windows: `C:\ProgramData\DockerDesktop\`

**Backup:** Use `backup.sh` or Docker volume backup tools.

### Bind Mounts (Host directories)

```yaml
./data       → /app/data        # Application data
./logs       → /app/logs        # Application logs
./ssh-keys   → /app/ssh-keys    # SSH keys (optional)
```

These are directly accessible on the host filesystem.

## Network Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                     Docker Bridge Network                    │
│                  (network-optimizer_default)                 │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│  ┌────────────────────┐         ┌──────────────────────┐    │
│  │ network-optimizer  │────────>│  influxdb:8086       │    │
│  │  (Web + API)       │         │  (internal)          │    │
│  │                    │         └──────────────────────┘    │
│  │  Exposed:          │                     ▲               │
│  │  - 8080 (Web UI)   │                     │               │
│  │  - 8081 (API)      │         ┌──────────────────────┐    │
│  └────────────────────┘         │  grafana:3000        │    │
│            │                    │  (dashboards)        │    │
│            │                    └──────────────────────┘    │
│            │                                                 │
│            └──> Writes metrics to InfluxDB                  │
│                 Grafana reads from InfluxDB                 │
│                                                              │
└─────────────────────────────────────────────────────────────┘
                         │
                         │ Port Mapping
                         ▼
                  Host Network
         8080 → Web UI
         8081 → Metrics API
         3000 → Grafana
         8086 → InfluxDB (optional)
```

**Internal DNS:**
- Services reference each other by name: `http://influxdb:8086`
- Grafana connects to InfluxDB via internal network
- No external network access needed for inter-service communication

## Port Mappings

| Service            | Internal Port | External Port | Description                |
|--------------------|---------------|---------------|----------------------------|
| Web UI             | 8080          | 8080          | Blazor application         |
| Metrics API        | 8081          | 8081          | Agent ingestion endpoint   |
| Grafana            | 3000          | 3000          | Dashboard interface        |
| InfluxDB           | 8086          | 8086*         | Database (optional expose) |

*InfluxDB exposed by default but can be restricted to internal network only.

## Environment Variables

### Required (Must Set)

```env
INFLUXDB_PASSWORD     # Admin password for InfluxDB
INFLUXDB_TOKEN        # API token for InfluxDB access
GRAFANA_PASSWORD      # Admin password for Grafana
```

### Optional (Have Defaults)

```env
WEB_PORT=8080
API_PORT=8081
GRAFANA_PORT=3000
INFLUXDB_PORT=8086
INFLUXDB_USERNAME=admin
INFLUXDB_ORG=network-optimizer
INFLUXDB_BUCKET=network_optimizer
INFLUXDB_RETENTION=30d
GRAFANA_USERNAME=admin
TZ=UTC
```

### Application-Specific

```env
UNIFI_CONTROLLER_URL    # Can also configure via UI
UNIFI_USERNAME          # Can also configure via UI
UNIFI_PASSWORD          # Can also configure via UI
LICENSE_KEY             # Can also configure via UI
AGENT_AUTH_TOKEN        # Auto-generated if not set
DEBUG=false
```

## Health Checks

All services include health checks:

### network-optimizer
```yaml
healthcheck:
  test: ["CMD", "curl", "-f", "http://localhost:8080/health"]
  interval: 30s
  timeout: 10s
  retries: 3
  start_period: 40s
```

### influxdb
```yaml
healthcheck:
  test: ["CMD", "influx", "ping"]
  interval: 30s
  timeout: 10s
  retries: 5
  start_period: 60s
```

### grafana
```yaml
healthcheck:
  test: ["CMD-SHELL", "curl -f http://localhost:3000/api/health || exit 1"]
  interval: 30s
  timeout: 10s
  retries: 3
  start_period: 60s
```

**Check status:**
```bash
docker-compose ps
```

Healthy services show "Up (healthy)" status.

## Metrics Data Flow

```
┌──────────────┐         ┌──────────────────┐         ┌──────────────┐
│ UDM/UCG      │  POST   │ Metrics API      │ Write   │  InfluxDB    │
│ Agent        │────────>│ :8081/api/metrics│────────>│  :8086       │
└──────────────┘         └──────────────────┘         └──────┬───────┘
                                                              │
┌──────────────┐         ┌──────────────────┐               │
│ Linux        │  POST   │ Metrics API      │ Write         │
│ Agent        │────────>│ :8081/api/metrics│──────────────>│
└──────────────┘         └──────────────────┘               │
                                                             │
┌──────────────┐         ┌──────────────────┐               │
│ SNMP Poller  │ Direct  │  InfluxDB        │               │
│              │────────>│  :8086           │<──────────────┘
└──────────────┘         └──────────────────┘               │
                                                             │
                         ┌──────────────────┐               │
                         │  Grafana         │  Query        │
                         │  :3000           │<──────────────┘
                         └──────────────────┘
                                  │
                                  │ View
                                  ▼
                           [User Browser]
```

## Security Considerations

### Secrets Management
- All passwords in `.env` file (not in version control)
- `.env` should be mode 600 (readable only by owner)
- Tokens generated with cryptographic randomness

### Network Isolation
- Services on isolated Docker network
- Only specified ports exposed to host
- Inter-service communication uses internal DNS

### Container Security
- Non-root users where possible
- Read-only root filesystem (where applicable)
- Minimal base images
- Health checks for automatic recovery

### Data Protection
- Volumes persist data
- Regular backups recommended
- Encrypted at rest if host filesystem is encrypted

## Troubleshooting Quick Reference

### Services Won't Start
```bash
docker-compose logs <service>
docker-compose ps
docker system df  # Check disk space
```

### Reset InfluxDB
```bash
docker-compose down
docker volume rm network-optimizer_influxdb-data
docker-compose up -d
```

### Reset Grafana
```bash
docker-compose down
docker volume rm network-optimizer_grafana-data
docker-compose up -d
```

### View Real-Time Logs
```bash
docker-compose logs -f --tail=100
```

### Check Resource Usage
```bash
docker stats
```

### Rebuild Containers
```bash
docker-compose down
docker-compose build --no-cache
docker-compose up -d
```

## Next Steps

1. **First Run:** Use `./start.sh` to get started quickly
2. **Configure:** Edit `.env` with your settings
3. **Access:** Open http://localhost:8080 in browser
4. **Monitor:** View Grafana dashboards at http://localhost:3000
5. **Backup:** Set up automated backups with cron + `backup.sh`
6. **Production:** See DEPLOYMENT.md for production hardening

## Additional Resources

- **README.md**: Quick start and basic usage
- **DEPLOYMENT.md**: Production deployment guide
- **docker-compose.yml**: Service definitions
- **.env.example**: All configuration options
- **Grafana Dashboards**: Pre-built JSON files

For support, see main project documentation or contact tj@ozarkconnect.net.
