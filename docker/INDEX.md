# Network Optimizer Docker Infrastructure - Index

Complete production-ready Docker infrastructure for the Ozark Connect Network Optimizer for UniFi.

## 📚 Documentation

Read these files in order:

1. **[README.md](README.md)** - Start here! Quick start guide and basic usage
2. **[QUICK-REFERENCE.md](QUICK-REFERENCE.md)** - Command cheat sheet
3. **[DEPLOYMENT.md](DEPLOYMENT.md)** - Production deployment guide
4. **[STRUCTURE.md](STRUCTURE.md)** - Detailed architecture documentation

## 🗂️ Directory Structure

```
docker/
├── README.md                           ⭐ Start here
├── QUICK-REFERENCE.md                  📋 Command cheat sheet
├── DEPLOYMENT.md                       🚀 Production guide
├── STRUCTURE.md                        📖 Architecture docs
├── INDEX.md                            📑 This file
│
├── Dockerfile                          🐳 Multi-stage .NET 9 build
├── docker-compose.yml                  🎼 Service orchestration
├── docker-compose.override.yml.example 🔧 Dev overrides template
├── entrypoint.sh                       🏁 Container startup
├── .env.example                        ⚙️  Environment template
├── .dockerignore                       🚫 Build optimization
│
├── start.sh                            ▶️  Quick start
├── stop.sh                             ⏹️  Stop services
├── reset.sh                            🔄 Reset everything
├── backup.sh                           💾 Create backup
├── restore.sh                          ♻️  Restore backup
│
└── grafana/
    ├── provisioning/
    │   ├── datasources/
    │   │   └── influxdb.yml           🔌 Auto-config InfluxDB
    │   └── dashboards/
    │       └── dashboards.yml         📊 Dashboard provisioning
    └── dashboards/
        ├── network-overview.json      📈 Health monitoring
        ├── sqm-performance.json       🚀 Bandwidth optimization
        ├── switch-deep-dive.json      🔍 Per-port analysis
        └── security-posture.json      🛡️  Security auditing
```

## 🚀 Quick Start

```bash
# 1. Navigate to docker directory
cd docker/

# 2. Quick start (generates secure passwords automatically)
./start.sh

# 3. Access services
# Web UI:  http://localhost:8080
# Grafana: http://localhost:3000
```

That's it! The start script handles everything.

## 📦 What's Included

### Core Services
- **Network Optimizer** - .NET 9 Blazor application
  - Web UI (port 8080)
  - Metrics API (port 8081)
  - SQLite database for configs
  - Agent deployment tools

- **InfluxDB 2.7** - Time-series database
  - Port 8086
  - 30-day retention (configurable)
  - Auto-initialized

- **Grafana Latest** - Dashboards
  - Port 3000
  - 4 pre-built dashboards
  - Auto-provisioned datasource

### Management Scripts
- `start.sh` - One-command deployment
- `stop.sh` - Graceful shutdown
- `backup.sh` - Complete backup
- `restore.sh` - Restore from backup
- `reset.sh` - Delete everything (with confirmation)

### Grafana Dashboards

#### 1. Network Overview
High-level health monitoring:
- Total devices
- SQM status
- Security score
- Critical issues
- Bandwidth trends
- Device status table

**Use:** Daily health check

#### 2. SQM Performance
Adaptive bandwidth management:
- Current vs baseline rate
- Latency monitoring
- Adjustment tracking
- Speedtest history

**Use:** Verify SQM optimization, diagnose ISP issues

#### 3. Switch Deep-Dive
Per-port network analysis:
- Port utilization
- Error rates
- PoE consumption
- Traffic patterns

**Use:** Troubleshoot bottlenecks, capacity planning

#### 4. Security Posture
Configuration audit tracking:
- Security score (0-100)
- Issue counts by severity
- Trends over time
- Detailed issue list

**Use:** Track security improvements, prioritize fixes

### Configuration
- `.env.example` - Complete environment template
- Secure password generation
- Customizable ports
- Timezone support
- Feature flags

## 🎯 Common Use Cases

### First-Time Setup
```bash
./start.sh
# Follow prompts
# Access http://localhost:8080
```

### Daily Operations
```bash
# Check status
docker-compose ps

# View logs
docker-compose logs -f

# Restart a service
docker-compose restart network-optimizer
```

### Backup & Recovery
```bash
# Create backup
./backup.sh

# Restore backup
./restore.sh backups/network-optimizer-backup-20240101-120000.tar.gz
```

### Updates
```bash
# Pull latest images
docker-compose pull

# Apply updates
docker-compose up -d

# Check logs
docker-compose logs -f
```

### Troubleshooting
```bash
# View all logs
docker-compose logs -f

# Restart everything
docker-compose restart

# Reset InfluxDB
docker-compose down
docker volume rm network-optimizer_influxdb-data
docker-compose up -d
```

## 🔧 Configuration Examples

### Change Ports
Edit `.env`:
```env
WEB_PORT=8090
GRAFANA_PORT=3030
```

Apply:
```bash
docker-compose up -d
```

### Adjust Data Retention
Edit `.env`:
```env
INFLUXDB_RETENTION=90d  # 90 days instead of 30
```

Apply:
```bash
docker-compose restart influxdb
```

### Set Timezone
Edit `.env`:
```env
TZ=America/Chicago
```

Apply:
```bash
docker-compose up -d
```

## 📊 Metrics Reference

### Data Sources
- UniFi API - Device status, client info
- SNMP - Switch/AP metrics
- Agents - SQM stats, speedtest, system metrics

### Measurements
- `sqm_stats` - Rate, baseline, latency
- `speedtest` - Download, upload, latency
- `device_metrics` - CPU, memory, uptime
- `interface_metrics` - Octets, errors, PoE
- `audit_score` - Security score
- `audit_issues` - Issue counts by severity

See STRUCTURE.md for complete schema.

## 🛡️ Security Features

- Isolated Docker network
- Secure password generation
- Environment-based secrets
- Health checks on all services
- Minimal container privileges
- Read-only configurations
- Regular backup support

## 📈 Resource Requirements

### Minimum
- 2GB RAM
- 10GB disk
- 1 CPU core

### Recommended
- 4GB RAM
- 50GB disk (for longer retention)
- 2 CPU cores
- SSD storage

### Scaling
Add resource limits in `docker-compose.override.yml`:
```yaml
services:
  network-optimizer:
    deploy:
      resources:
        limits:
          cpus: '2.0'
          memory: 2G
```

## 🌐 Network Requirements

### Outbound
- UniFi Controller (typically local network)
- Docker Hub (for image pulls)
- NTP servers (for time sync)

### Inbound (for agents)
- Port 8081 (Metrics API)

### Ports Used
- 8080 - Web UI
- 8081 - Metrics API
- 3000 - Grafana
- 8086 - InfluxDB

## 📱 Platform Support

### Tested Platforms
- ✅ Ubuntu 20.04+
- ✅ Debian 11+
- ✅ macOS 11+
- ✅ Windows 10/11 with Docker Desktop
- ✅ Synology NAS (Container Manager)
- ✅ QNAP NAS (Container Station)
- ✅ Unraid
- ✅ Proxmox VE

### Cloud Platforms
- ✅ AWS EC2
- ✅ Google Cloud
- ✅ DigitalOcean
- ✅ Linode
- ✅ Vultr

## 🔍 Monitoring

### Built-in Health Checks
All services include Docker health checks:
```bash
docker-compose ps
```

### External Monitoring
Monitor these endpoints:
- `http://your-server:8080/health` - Web UI
- `http://your-server:3000/api/health` - Grafana
- `http://your-server:8086/health` - InfluxDB

### Logging
```bash
# Real-time logs
docker-compose logs -f

# Specific service
docker-compose logs -f network-optimizer

# Last 100 lines
docker-compose logs --tail=100
```

## 🚨 Troubleshooting Flowchart

```
Service won't start?
├─ Check logs: docker-compose logs <service>
├─ Check disk space: df -h
├─ Check permissions: ls -la data/ logs/
└─ Reset: docker-compose down && docker-compose up -d

Data not appearing?
├─ Check InfluxDB: docker-compose logs influxdb
├─ Verify agent connectivity: curl http://localhost:8081/health
├─ Check Grafana datasource: Grafana → Configuration → Data Sources
└─ Restart: docker-compose restart

Dashboard not loading?
├─ Check Grafana: docker-compose logs grafana
├─ Verify files exist: ls -la grafana/dashboards/
├─ Re-provision: docker-compose restart grafana
└─ Manual import: Grafana UI → Dashboards → Import

High memory usage?
├─ Check usage: docker stats
├─ Reduce retention: INFLUXDB_RETENTION=7d in .env
├─ Restart services: docker-compose restart
└─ Add limits: See STRUCTURE.md

Port conflicts?
├─ Check ports: netstat -tuln | grep <port>
├─ Change in .env: WEB_PORT=8090
├─ Apply: docker-compose up -d
└─ Verify: docker-compose ps
```

## 💡 Pro Tips

### Performance
- Use SSD for Docker volumes
- Set up automated backups
- Monitor disk usage regularly
- Adjust retention based on needs

### Security
- Change default passwords
- Use reverse proxy with SSL
- Restrict network access
- Keep Docker updated

### Reliability
- Set up monitoring
- Configure log rotation
- Test restore procedure
- Document customizations

### Efficiency
- Use docker-compose override for dev
- Bookmark Grafana dashboards
- Set up shell aliases
- Keep .env in version control (encrypted)

## 📞 Getting Help

1. **Check Documentation**
   - README.md - Basic usage
   - QUICK-REFERENCE.md - Commands
   - DEPLOYMENT.md - Production
   - STRUCTURE.md - Deep dive

2. **Check Logs**
   ```bash
   docker-compose logs -f
   ```

3. **Community**
   - GitHub Issues
   - Documentation site
   - Email support

4. **Emergency**
   ```bash
   # Reset everything
   ./reset.sh

   # Restore from backup
   ./restore.sh backups/latest.tar.gz
   ```

## 🎓 Learning Path

### Beginner
1. Run `./start.sh`
2. Access Web UI
3. Explore Grafana dashboards
4. Read README.md

### Intermediate
1. Customize `.env`
2. Set up backups
3. Configure reverse proxy
4. Read DEPLOYMENT.md

### Advanced
1. Custom dashboards
2. High availability setup
3. Resource optimization
4. Read STRUCTURE.md

## 🔄 Maintenance Schedule

### Daily
- Check service health
- Review critical alerts

### Weekly
- Review logs
- Check disk usage
- Verify backups

### Monthly
- Update images
- Security review
- Performance tuning

### Quarterly
- Test restore procedure
- Audit configuration
- Review documentation

## 📝 Changelog

Track changes in your deployment:

```bash
# Tag current state
git tag -a v1.0.0 -m "Initial production deployment"

# Document changes
echo "$(date): Deployed v1.0.0" >> CHANGELOG.md
```

## ✅ Pre-Production Checklist

- [ ] `.env` configured with secure passwords
- [ ] Ports configured for your environment
- [ ] Timezone set correctly
- [ ] Backups configured and tested
- [ ] Firewall rules in place
- [ ] SSL/HTTPS configured (if needed)
- [ ] Monitoring set up
- [ ] Documentation updated
- [ ] Team trained on operations
- [ ] Restore procedure tested

## 🎉 Success Criteria

After deployment, verify:
- ✅ All services show "healthy" status
- ✅ Web UI accessible and responsive
- ✅ Grafana showing data in dashboards
- ✅ Backups completing successfully
- ✅ Logs clean (no errors)
- ✅ Resource usage acceptable
- ✅ Team can perform basic operations

## 📚 Additional Resources

- **Docker Documentation**: https://docs.docker.com
- **InfluxDB Documentation**: https://docs.influxdata.com
- **Grafana Documentation**: https://grafana.com/docs
- **Project Repository**: https://github.com/ozark-connect/network-optimizer
- **Support**: support@ozark-connect.com

---

**Version**: 1.0.0
**Last Updated**: 2025-12-08
**License**: See main project LICENSE

**Ready to deploy?** Start with `./start.sh` and refer to this index as needed!
