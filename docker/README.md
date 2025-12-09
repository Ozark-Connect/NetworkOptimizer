# Network Optimizer Docker Deployment

Complete Docker infrastructure for the Ozark Connect Network Optimizer for UniFi.

## Quick Start

1. **Copy the environment template:**
   ```bash
   cd docker
   cp .env.example .env
   ```

2. **Edit `.env` and set secure passwords:**
   ```bash
   # Required: Change these!
   INFLUXDB_PASSWORD=your_secure_password_here
   INFLUXDB_TOKEN=your_secure_token_here
   GRAFANA_PASSWORD=your_secure_password_here
   ```

3. **Start the stack:**
   ```bash
   docker-compose up -d
   ```

4. **Access the services:**
   - Network Optimizer Web UI: http://localhost:8080
   - Grafana Dashboards: http://localhost:3000 (admin / your_grafana_password)
   - InfluxDB: http://localhost:8086 (admin / your_influxdb_password)
   - Metrics API: http://localhost:8081

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                    Docker Compose Stack                         │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌──────────────────┐  ┌──────────────┐  ┌──────────────────┐  │
│  │ Network Optimizer│  │  InfluxDB    │  │    Grafana       │  │
│  │                  │  │  Time-Series │  │   Dashboards     │  │
│  │  - Web UI :8080  │◄─┤   Database   │◄─┤                  │  │
│  │  - API :8081     │  │    :8086     │  │     :3000        │  │
│  └──────────────────┘  └──────────────┘  └──────────────────┘  │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

## Services

### Network Optimizer (Port 8080, 8081)

The main application providing:
- **Web UI (8080)**: Blazor Server web interface
  - Dashboard and monitoring
  - SQM configuration and management
  - Security audit results
  - Agent deployment wizard
  - Report generation

- **Metrics API (8081)**: Agent ingestion endpoint
  - Receives metrics from distributed agents
  - Validates agent authentication
  - Writes to InfluxDB

**Volumes:**
- `./data` → `/app/data` - SQLite database, configurations, license
- `./ssh-keys` → `/app/ssh-keys` - SSH keys for agent deployment (optional)
- `./logs` → `/app/logs` - Application logs

### InfluxDB (Port 8086)

Time-series database for metrics storage:
- Stores all monitoring data
- 30-day default retention (configurable)
- Auto-initialized with bucket and organization

**Volumes:**
- `influxdb-data` - Database files
- `influxdb-config` - Configuration

### Grafana (Port 3000)

Pre-configured dashboards for visualization:
- Auto-provisioned InfluxDB datasource
- Four production-ready dashboards:
  1. **Network Overview** - High-level health monitoring
  2. **SQM Performance** - Bandwidth, latency, speedtest history
  3. **Switch Deep-Dive** - Per-port utilization and PoE
  4. **Security Posture** - Audit scores and issue trends

**Volumes:**
- `grafana-data` - Dashboard storage
- `./grafana/provisioning` - Auto-provisioning configs
- `./grafana/dashboards` - Dashboard JSON files

## Configuration

### Environment Variables

See `.env.example` for all available options. Key variables:

#### Ports
```env
WEB_PORT=8080           # Blazor web UI
API_PORT=8081           # Metrics ingestion API
INFLUXDB_PORT=8086      # InfluxDB
GRAFANA_PORT=3000       # Grafana
```

#### InfluxDB
```env
INFLUXDB_USERNAME=admin
INFLUXDB_PASSWORD=changeme_influxdb_password
INFLUXDB_TOKEN=changeme_influxdb_token
INFLUXDB_ORG=network-optimizer
INFLUXDB_BUCKET=network_optimizer
INFLUXDB_RETENTION=30d  # Or 90d, 1y, 0 (infinite)
```

#### Grafana
```env
GRAFANA_USERNAME=admin
GRAFANA_PASSWORD=changeme_grafana_password
```

#### Timezone
```env
TZ=America/Chicago  # Or your timezone
```

### Volume Mounts

#### Persistent Data
The `./data` directory contains:
- SQLite database (configs, audit results, license)
- Agent templates
- Application state

**Backup:** Regular backups of `./data` and InfluxDB volumes recommended.

#### SSH Keys (Optional)
Place SSH keys in `./ssh-keys/` for automated agent deployment:
```bash
./ssh-keys/
├── id_rsa          # Private key
└── id_rsa.pub      # Public key
```

Set permissions:
```bash
chmod 600 ./ssh-keys/id_rsa
chmod 644 ./ssh-keys/id_rsa.pub
```

## Dashboards

### 1. Network Overview
High-level monitoring dashboard showing:
- Total devices online
- SQM status
- Security score gauge
- Critical issues count
- Bandwidth trends
- Device status table
- WAN latency graph

**Use case:** Daily health check, at-a-glance status

### 2. SQM Performance
Detailed adaptive bandwidth management:
- Current rate vs baseline
- Latency gauge with thresholds
- Last adjustment status
- Rate trends over time
- Speedtest history (scatter plot and table)
- Adjustment frequency

**Variables:**
- Device selector (UDM/UCG)
- Interface selector (WAN interface)

**Use case:** Verify SQM is optimizing properly, diagnose ISP issues

### 3. Switch Deep-Dive
Per-port analysis and troubleshooting:
- Active ports count
- Switch CPU and memory
- Total PoE power consumption
- Port status table with utilization
- Per-port RX/TX graphs
- Error rates
- PoE trends

**Variables:**
- Switch selector
- Port selector

**Use case:** Troubleshoot network issues, capacity planning, identify chatty devices

### 4. Security Posture
Security audit tracking:
- Overall security score gauge (0-100)
- Critical/Warning/Info issue counts
- Score trend over time
- Issues by severity (stacked graph)
- Issues by category (bar chart)
- Current issues table with details
- Issue distribution pie chart
- Audit history

**Use case:** Track security improvements, prioritize remediation

## Management

### Starting the Stack
```bash
docker-compose up -d
```

### Stopping the Stack
```bash
docker-compose down
```

### View Logs
```bash
# All services
docker-compose logs -f

# Specific service
docker-compose logs -f network-optimizer
docker-compose logs -f influxdb
docker-compose logs -f grafana
```

### Restart a Service
```bash
docker-compose restart network-optimizer
```

### Update Images
```bash
docker-compose pull
docker-compose up -d
```

### Health Checks
All services include health checks:
```bash
docker-compose ps
```

Healthy output:
```
NAME                          STATUS
network-optimizer             Up (healthy)
network-optimizer-influxdb    Up (healthy)
network-optimizer-grafana     Up (healthy)
```

## Troubleshooting

### Service Won't Start

**Check logs:**
```bash
docker-compose logs <service-name>
```

**Common issues:**
1. **Port conflicts:** Another service using 8080, 8086, or 3000
   - Solution: Change ports in `.env`
2. **Permission errors:** Cannot write to volumes
   - Solution: `chmod` the directories or check Docker volume permissions
3. **Missing `.env`:** Environment variables not set
   - Solution: Copy `.env.example` to `.env`

### InfluxDB Not Initializing

**Symptom:** Grafana can't connect to InfluxDB

**Solution:**
```bash
# Remove volumes and reinitialize
docker-compose down -v
docker-compose up -d
```

**Note:** This deletes all metrics data. Backup first if needed.

### Grafana Dashboards Not Loading

**Check datasource:**
1. Go to Grafana → Configuration → Data Sources
2. Verify "InfluxDB-NetworkOptimizer" exists
3. Click "Test" - should show "datasource is working"

**If missing:**
```bash
# Restart Grafana to re-provision
docker-compose restart grafana
```

### Agent Metrics Not Appearing

**Verify API is accessible:**
```bash
curl http://localhost:8081/health
```

**Check agent authentication:**
- Agents need valid `AGENT_AUTH_TOKEN` from `.env`
- Check application logs for authentication errors

### Reset Everything

**Complete reset (deletes all data):**
```bash
docker-compose down -v
rm -rf data/
docker-compose up -d
```

## Security Considerations

### Production Deployment

1. **Change default passwords** in `.env`
2. **Use strong tokens** for `INFLUXDB_TOKEN` and `AGENT_AUTH_TOKEN`
   ```bash
   openssl rand -base64 32
   ```
3. **Restrict network access:**
   - Use firewall rules to limit who can access ports
   - Consider reverse proxy with SSL (nginx, Caddy, Traefik)
4. **Enable HTTPS** with reverse proxy:
   ```nginx
   server {
       listen 443 ssl;
       server_name network-optimizer.example.com;

       ssl_certificate /path/to/cert.pem;
       ssl_certificate_key /path/to/key.pem;

       location / {
           proxy_pass http://localhost:8080;
           proxy_http_version 1.1;
           proxy_set_header Upgrade $http_upgrade;
           proxy_set_header Connection "upgrade";
       }
   }
   ```
5. **Backup regularly:**
   - `./data` directory
   - InfluxDB volume: `docker run --rm -v network-optimizer_influxdb-data:/data -v $(pwd):/backup ubuntu tar czf /backup/influxdb-backup.tar.gz /data`

### Network Isolation

Docker Compose creates an isolated network. Services communicate internally:
- `network-optimizer` → `influxdb:8086`
- `grafana` → `influxdb:8086`

External access only through exposed ports.

## Integration with Agents

### Agent Configuration

Agents send metrics to the API endpoint:
```bash
METRICS_API_URL=http://your-server:8081/api/metrics
AGENT_AUTH_TOKEN=your_agent_token_from_env
```

### Supported Agents

1. **UDM/UCG Agent** - Collects SQM stats, speedtest results
2. **Linux Agent** - System metrics, Docker stats
3. **SNMP Poller** - Switch/AP metrics via SNMP

See main documentation for agent deployment.

## Performance Tuning

### InfluxDB Retention

Adjust retention based on storage capacity:
```env
# Short retention (small storage)
INFLUXDB_RETENTION=7d

# Long retention (ample storage)
INFLUXDB_RETENTION=1y

# Infinite retention
INFLUXDB_RETENTION=0
```

### Resource Limits

Add resource limits to `docker-compose.yml`:
```yaml
services:
  network-optimizer:
    deploy:
      resources:
        limits:
          cpus: '1.0'
          memory: 1G
        reservations:
          cpus: '0.5'
          memory: 512M
```

## Upgrading

### Minor Updates

```bash
docker-compose pull
docker-compose up -d
```

Data persists in volumes.

### Major Updates

1. **Backup data:**
   ```bash
   tar czf backup-$(date +%Y%m%d).tar.gz data/
   ```

2. **Stop services:**
   ```bash
   docker-compose down
   ```

3. **Pull new images:**
   ```bash
   docker-compose pull
   ```

4. **Start services:**
   ```bash
   docker-compose up -d
   ```

5. **Check logs:**
   ```bash
   docker-compose logs -f
   ```

## Support

For issues, feature requests, or questions:
- GitHub: https://github.com/ozark-connect/network-optimizer
- Documentation: https://docs.ozark-connect.com/network-optimizer
- Support: support@ozark-connect.com

## License

See main project LICENSE file.
