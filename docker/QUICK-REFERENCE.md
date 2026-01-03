# Network Optimizer - Quick Reference Card

## 🚀 Quick Start

### macOS
```bash
cd docker
docker compose -f docker-compose.macos.yml build
docker compose -f docker-compose.macos.yml up -d
```

### Linux / Windows
```bash
cd docker
docker compose up -d
```

Access at: http://localhost:8042

## 📋 Common Commands

### Service Management
```bash
./start.sh              # Start all services
./stop.sh               # Stop all services
docker-compose restart  # Restart all services
docker-compose ps       # Check status
```

### Logs
```bash
docker-compose logs -f                    # All services
docker-compose logs -f network-optimizer  # Web UI
docker-compose logs -f influxdb          # Database
docker-compose logs -f grafana           # Dashboards
```

### Backup & Restore
```bash
./backup.sh                              # Create backup
./restore.sh backups/backup-*.tar.gz    # Restore backup
./reset.sh                               # Delete everything (WARNING!)
```

### Updates
```bash
docker-compose pull     # Pull latest images
docker-compose up -d    # Apply updates
```

## 🌐 Access URLs

| Service | URL | Credentials |
|---------|-----|-------------|
| Web UI | http://localhost:8042 | Configure on first run |
| Grafana | http://localhost:3000 | admin / (see .env) |
| InfluxDB | http://localhost:8086 | admin / (see .env) |

## 📊 Grafana Dashboards

1. **Network Overview** - High-level health check
2. **SQM Performance** - Bandwidth optimization metrics
3. **Switch Deep-Dive** - Per-port analysis
4. **Security Posture** - Audit scores and issues

## 🔧 Configuration

**Environment File:** `.env`

```bash
# Edit configuration
nano .env

# Apply changes
docker-compose up -d
```

**Key Settings:**
- `INFLUXDB_PASSWORD` - Database password
- `INFLUXDB_TOKEN` - API token
- `GRAFANA_PASSWORD` - Dashboard password
- `INFLUXDB_RETENTION` - Data retention (7d, 30d, 90d, 1y, 0)
- `TZ` - Timezone

## 🛠️ Troubleshooting

### Services Won't Start
```bash
docker-compose down
docker-compose up -d
docker-compose logs -f
```

### High Memory Usage
```bash
# Check usage
docker stats

# Reduce retention
# Edit .env: INFLUXDB_RETENTION=7d
docker-compose restart influxdb
```

### Port Already in Use
```bash
# Edit .env and change ports:
WEB_PORT=8090
GRAFANA_PORT=3030
INFLUXDB_PORT=8096

docker-compose up -d
```

### Reset Everything
```bash
./reset.sh              # Deletes ALL data!
./start.sh              # Fresh start
```

### InfluxDB Not Working
```bash
docker-compose down -v
docker-compose up -d
```

### Grafana Dashboard Missing
```bash
docker-compose restart grafana
# Wait 30 seconds
# Refresh Grafana in browser
```

## 📁 Important Files

| File | Purpose |
|------|---------|
| `.env` | Configuration (passwords, ports) |
| `data/` | Application data (SQLite) |
| `logs/` | Application logs |
| `ssh-keys/` | SSH keys for agent deployment |
| `backups/` | Backup archives |

## 🔐 Security Checklist

- [ ] Changed default passwords in `.env`
- [ ] `.env` file permissions: `chmod 600 .env`
- [ ] Regular backups enabled
- [ ] Firewall configured
- [ ] HTTPS reverse proxy (production)
- [ ] Strong `INFLUXDB_TOKEN` and `AGENT_AUTH_TOKEN`

## 📈 Metrics Schema

### SQM Stats
```
sqm_stats,device=udm-pro,interface=eth2
  rate=265.5,baseline=270.0,latency=18.2,adjustment="none"
```

### Speedtest
```
speedtest,device=udm-pro,interface=eth2,server="Cox"
  download=285.4,upload=35.2,latency=12.5
```

### Device Metrics
```
device_metrics,device=usw-24-poe,type=switch
  cpu=12.5,memory_used=45.2,uptime=864000
```

### Interface Metrics
```
interface_metrics,device=usw-24-poe,port=1,port_name="Uplink"
  in_octets=123456,out_octets=987654,poe_power=15.2
```

### Audit Metrics
```
audit_score
  score=85

audit_issues,severity=critical,category=firewall
  count=2,description="..."
```

## 🔌 Agent Configuration

Agents send metrics to API:

```bash
METRICS_API_URL=http://your-server:8081/api/metrics
AGENT_AUTH_TOKEN=your_token_from_env
```

**Supported Agents:**
- UDM/UCG Gateway Agent (SQM, speedtest)
- Linux System Agent (system metrics)
- SNMP Poller (switch/AP metrics)

## 📦 Backup Contents

**Automatic Backup Includes:**
- Application data (`data/`)
- InfluxDB time-series data
- Grafana dashboards and settings
- Environment configuration (`.env`)

**Excluded:**
- Docker images (pull fresh)
- Log files (optional)
- Temporary files

## 🐳 Docker Commands

### Container Management
```bash
docker-compose up -d           # Start in background
docker-compose down            # Stop and remove
docker-compose restart <svc>   # Restart specific service
docker-compose exec <svc> bash # Shell into container
```

### Monitoring
```bash
docker-compose ps              # Status
docker-compose top             # Processes
docker stats                   # Resource usage
docker system df               # Disk usage
```

### Cleanup
```bash
docker system prune            # Remove unused objects
docker volume prune            # Remove unused volumes
docker image prune             # Remove unused images
```

## 🆘 Emergency Recovery

### Restore from Backup
```bash
./restore.sh backups/network-optimizer-backup-YYYYMMDD-HHMMSS.tar.gz
```

### Manual InfluxDB Export
```bash
docker exec network-optimizer-influxdb influxd backup /tmp/manual-backup
docker cp network-optimizer-influxdb:/tmp/manual-backup ./manual-backup/
```

### Export Grafana Dashboards
```bash
# Via UI: Dashboard → Share → Export → Save to file
# Or API:
curl -H "Authorization: Bearer YOUR_API_KEY" \
  http://localhost:3000/api/dashboards/uid/DASHBOARD_UID
```

## 📞 Getting Help

- **Documentation**: README.md, DEPLOYMENT.md, STRUCTURE.md
- **Logs**: `docker-compose logs -f`
- **Health**: `docker-compose ps`
- **GitHub**: https://github.com/tvancott42/NetworkOptimizer
- **Support**: tj@ozarkconnect.net

## 🎯 Performance Tuning

### Reduce Memory
```env
# .env
INFLUXDB_RETENTION=7d   # Shorter retention
```

### Increase Performance
- Use SSD for volumes
- Increase Docker memory limit
- Optimize InfluxDB queries in dashboards

### Resource Limits
```yaml
# docker-compose.override.yml
services:
  network-optimizer:
    deploy:
      resources:
        limits:
          cpus: '2'
          memory: 2G
```

## ✅ Health Check Endpoints

- Web UI: `http://localhost:8042/api/health`
- Grafana: `http://localhost:3000/api/health`
- InfluxDB: `http://localhost:8086/health`

## 🔄 Update Workflow

1. **Backup** → `./backup.sh`
2. **Stop** → `docker-compose down`
3. **Pull** → `docker-compose pull`
4. **Start** → `docker-compose up -d`
5. **Verify** → `docker-compose ps` and check logs
6. **Test** → Access UI and verify functionality

## 💾 Disk Space Management

### Check Usage
```bash
docker system df
du -sh data/ logs/
```

### Clean Up
```bash
# Remove old logs
find logs/ -name "*.log" -mtime +30 -delete

# Reduce InfluxDB retention
# Edit .env: INFLUXDB_RETENTION=14d
docker-compose restart influxdb

# Clean Docker
docker system prune -a
```

## 🌍 Timezone Configuration

```env
# .env
TZ=America/Chicago
TZ=America/New_York
TZ=Europe/London
TZ=Asia/Tokyo
```

**Apply:**
```bash
docker-compose up -d
```

## 🔑 Generate Secure Tokens

```bash
# InfluxDB token
openssl rand -base64 32

# Agent auth token
openssl rand -hex 32

# Password
openssl rand -base64 24
```

---

**Quick Help:** Run any script with `-h` or `--help` for usage info.

**Version Check:**
```bash
docker-compose version
docker --version
```

**System Requirements:**
- Docker 20.10+
- Docker Compose 2.0+
- 2GB RAM minimum
- 10GB disk minimum
