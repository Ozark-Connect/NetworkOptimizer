# Claude Code Project Notes

## Local Development (Windows)

**Note:** Local Docker container removed for security. Use NAS deployment for testing.

### Run with Docker
```bash
cd docker
docker compose build --no-cache network-optimizer
docker compose up -d
```

### Run without Docker
```bash
cd src/NetworkOptimizer.Web
dotnet run
```
Access at http://localhost:5000

### Build Only
```bash
dotnet build
```

### Publish
```bash
cd src/NetworkOptimizer.Web
dotnet publish -c Release -o C:/NetworkOptimizer
```

## Production Deployment

**Production Server:** `root@nas:/opt/network-optimizer`

### Deploy Process

1. Commit and push changes:
   ```bash
   git add -A && git commit -m "message" && git push
   ```

2. SSH to NAS and pull:
   ```bash
   ssh root@nas "cd /opt/network-optimizer && git pull"
   ```

3. Rebuild and restart Docker:
   ```bash
   ssh root@nas "cd /opt/network-optimizer/docker && docker compose -f docker-compose.linux.yml build --no-cache network-optimizer && docker compose -f docker-compose.linux.yml up -d network-optimizer"
   ```

### Quick Deploy (all-in-one)
```bash
git push && ssh root@nas "cd /opt/network-optimizer && git pull && cd docker && docker compose -f docker-compose.linux.yml build --no-cache network-optimizer && docker compose -f docker-compose.linux.yml up -d network-optimizer"
```

### First-Time Setup
Create `.env` from template:
```bash
ssh root@nas "cd /opt/network-optimizer/docker && cp .env.example .env"
# Then edit .env to set secure passwords
```

### Services

| Service | Port | URL |
|---------|------|-----|
| Web UI | 8042 | http://localhost:8042 (localhost only) |

### Check Status
```bash
ssh root@nas "docker ps --filter 'name=network-optimizer'"
```

### View Logs
```bash
ssh root@nas "docker logs -f network-optimizer"
```

## Project Structure

- `src/NetworkOptimizer.Web` - Blazor web UI (main app)
- `src/NetworkOptimizer.Audit` - Security audit engine
- `src/NetworkOptimizer.UniFi` - UniFi API client
- `src/NetworkOptimizer.Storage` - SQLite database models
- `src/NetworkOptimizer.Monitoring` - SNMP/SSH polling
- `docker/` - Docker deployment files

## Key Files

- `docker/docker-compose.yml` - Full stack (app + InfluxDB + Grafana)
- `docker/docker-compose.linux.yml` - Lightweight (app only, host networking)
- `docker/Dockerfile` - Container build

## Static File Downloads

Files in `src/NetworkOptimizer.Web/wwwroot/downloads/` are served at `/downloads/`

Example: `/downloads/iperf3_3.18-1_mips-3.4.ipk`
