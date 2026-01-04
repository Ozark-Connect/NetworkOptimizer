# Network Optimizer - Quick Reference Card

## Quick Start

### macOS
```bash
cd docker
docker compose -f docker-compose.macos.yml build
docker compose -f docker-compose.macos.yml up -d
```

### Linux / Windows
```bash
cd docker
docker compose build
docker compose up -d
```

### First Run - Get Admin Password
```bash
docker compose logs network-optimizer | grep -A5 "FIRST-RUN"
```

Access at: **http://localhost:8042**

## Common Commands

### Service Management
```bash
docker-compose up -d        # Start
docker-compose down         # Stop
docker-compose restart      # Restart
docker-compose ps           # Check status
```

### Logs
```bash
docker-compose logs -f network-optimizer
```

### Updates
```bash
docker-compose down
docker-compose pull
docker-compose up -d
```

## Configuration

**Environment File:** `.env` (optional)

```bash
cp .env.example .env
nano .env
docker-compose up -d
```

**Key Settings:**
```env
WEB_PORT=8042              # Web UI port
TZ=America/Chicago         # Timezone
APP_PASSWORD=              # Optional preset password
HOST_IP=                   # Required for bridge networking
```

## Admin Password

**Priority order:**
1. Database password (Settings → Admin Password) - recommended
2. `APP_PASSWORD` environment variable
3. Auto-generated on first run (check logs)

**Set permanent password:**
1. Log in with auto-generated password from logs
2. Go to Settings → Admin Password
3. Enter and save new password

## Troubleshooting

### Service Won't Start
```bash
docker-compose down
docker-compose up -d
docker-compose logs -f network-optimizer
```

### Port Already in Use
```bash
# Edit .env
WEB_PORT=8090

docker-compose up -d
```

### Reset Everything
```bash
docker-compose down -v
rm -rf data/
docker-compose up -d
```

## Client Speed Testing

### Browser-Based (OpenSpeedTest™)
Access at: **http://localhost:3005**

Configure result reporting in `.env`:
```env
HOST_IP=192.168.1.100       # or
HOST_NAME=nas               # or
REVERSE_PROXIED_HOST_NAME=optimizer.example.com
```

To disable: comment out `openspeedtest` service in `docker-compose.yml`

### CLI-Based (iperf3)
Enable in `.env`:
```env
IPERF3_SERVER_ENABLED=true
```

Test from clients:
```bash
iperf3 -c your-server      # Download
iperf3 -c your-server -R   # Upload
```

## Important Files

| File | Purpose |
|------|---------|
| `.env` | Configuration (optional) |
| `data/` | SQLite database, credentials |
| `logs/` | Application logs |
| `ssh-keys/` | SSH keys for device access |

## Health Check

```bash
docker-compose ps
curl http://localhost:8042/api/health
```

## Backup & Restore

### Backup
```bash
tar czf backup-$(date +%Y%m%d).tar.gz data/
```

### Restore
```bash
docker-compose down
tar xzf backup-YYYYMMDD.tar.gz
docker-compose up -d
```

## Security Checklist

- [ ] Set permanent password in Settings
- [ ] Firewall configured (allow 8042/tcp)
- [ ] HTTPS via reverse proxy (production)
- [ ] Regular backups of `data/` directory

## Docker Commands

```bash
docker-compose up -d           # Start in background
docker-compose down            # Stop and remove
docker-compose restart         # Restart
docker-compose exec network-optimizer bash  # Shell into container
docker stats                   # Resource usage
docker system prune            # Clean up unused objects
```

## Getting Help

- **Logs**: `docker-compose logs -f network-optimizer`
- **Health**: `curl http://localhost:8042/api/health`
- **GitHub**: https://github.com/Ozark-Connect/NetworkOptimizer

## System Requirements

- Docker 20.10+
- Docker Compose 2.0+
- 1GB RAM minimum
- 500MB disk minimum
