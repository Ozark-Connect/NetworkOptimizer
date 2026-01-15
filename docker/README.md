# Network Optimizer Docker Deployment

Complete Docker infrastructure for the Ozark Connect Network Optimizer for UniFi.

## Quick Start

### Option A: Pull Docker Image (Recommended)

The fastest way to get started. No build required.

**Linux / Windows:**
```bash
mkdir network-optimizer && cd network-optimizer
curl -o docker-compose.yml https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/docker/docker-compose.prod.yml
curl -O https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/docker/.env.example
cp .env.example .env
docker compose up -d
```

**macOS:**
```bash
mkdir network-optimizer && cd network-optimizer
curl -O https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/docker/docker-compose.macos.yml
curl -O https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/docker/.env.example
cp .env.example .env
docker compose -f docker-compose.macos.yml up -d
```

### Option B: Build from Source

Clone the repository and build locally.

**Linux / Windows:**
```bash
git clone https://github.com/Ozark-Connect/NetworkOptimizer.git
cd NetworkOptimizer/docker
cp .env.example .env
docker compose build
docker compose up -d
```

**macOS:**

macOS doesn't support `network_mode: host`, so use the macOS-specific compose file:

```bash
git clone https://github.com/Ozark-Connect/NetworkOptimizer.git
cd NetworkOptimizer/docker
cp .env.example .env
docker compose -f docker-compose.macos.yml build
docker compose -f docker-compose.macos.yml up -d
```

### First Run

1. **Get the auto-generated admin password:**
   ```bash
   docker logs network-optimizer 2>&1 | grep -A5 "AUTO-GENERATED"
   ```
   On first run, a secure password is generated and displayed in the logs.

2. **Access the Web UI:**
   - Network Optimizer: http://localhost:8042 (use password from logs)
   - Wait ~60 seconds on first startup

3. **Set a permanent password:**
   After logging in, go to Settings → Admin Password to set your own password (recommended).

**No `.env` file required** - defaults work out of the box. Optionally edit `.env` to set `APP_PASSWORD` or timezone.

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                    Docker Compose Stack                         │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │                   Network Optimizer                       │  │
│  │  - Blazor Web UI :8042                                    │  │
│  │  - iperf3 Server :5201 (optional)                         │  │
│  │  - SQLite Database (persistent in ./data)                 │  │
│  │  - Security Auditing, SQM, Speed Tests                    │  │
│  └──────────────────────────────────────────────────────────┘  │
│                                                                 │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │                   OpenSpeedTest                           │  │
│  │  - Browser-based speed test :3005 (configurable)          │  │
│  │  - Results sent to Network Optimizer API                  │  │
│  └──────────────────────────────────────────────────────────┘  │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

## Services

### Network Optimizer (Port 8042)

The main application providing:
- **Web UI**: Blazor Server web interface
  - Dashboard and monitoring
  - SQM configuration and management
  - Security audit results
  - Speed testing with path analysis
  - Report generation

**Volumes:**
- `./data` → `/app/data` - SQLite database, configurations
- `./ssh-keys` → `/app/ssh-keys` - SSH keys for agent deployment (optional)
- `./logs` → `/app/logs` - Application logs

### OpenSpeedTest (Port 3005, configurable)

Browser-based speed testing from any device. Results are automatically sent to Network Optimizer.

**Configuration:** Set `HOST_NAME` in `.env` for canonical URL enforcement and friendlier URLs. Set `HOST_IP` if path analysis can't auto-detect the server IP (bridge networking). See [Client Speed Testing](DEPLOYMENT.md#client-speed-testing-optional) for details.

**To disable:** Comment out the `openspeedtest` service in `docker-compose.yml`.

See [Client Speed Testing](DEPLOYMENT.md#client-speed-testing-optional) for full setup details.

## Admin Authentication

The web UI requires authentication. Password sources (in priority order):

1. **Database password** - Set via Settings → Admin Password (recommended)
2. **Environment variable** - Set `APP_PASSWORD` in `.env`
3. **Auto-generated** - On first run, a secure password is generated and shown in logs

### First Run
```bash
# View the auto-generated password (shown only once on first startup)
docker logs network-optimizer 2>&1 | grep -A5 "AUTO-GENERATED"
```

### Setting a Permanent Password
1. Log in with the auto-generated password
2. Go to Settings → Admin Password
3. Enter and confirm your new password
4. Click Save

### Using Environment Variable
Alternatively, set `APP_PASSWORD` in `.env`:
```env
APP_PASSWORD=your_secure_password
```

**Note:** Database passwords override the environment variable. Clear the database password in Settings to use `APP_PASSWORD`.

### Reset Admin Password

If you've forgotten your password or need to reset it:

```bash
# Clear the password from the database
docker exec network-optimizer sqlite3 /app/data/network_optimizer.db "UPDATE AdminSettings SET Password = NULL;"

# Restart to trigger auto-generated password
cd /path/to/network-optimizer/docker
docker compose up -d

# View the new auto-generated password
docker logs network-optimizer 2>&1 | grep -A5 "AUTO-GENERATED"
```

## Configuration

### Environment Variables

See `.env.example` for all available options. Key variables:

```env
WEB_PORT=8042           # Blazor web UI (default)
TZ=America/Chicago      # Your timezone
APP_PASSWORD=           # Optional: preset admin password (otherwise auto-generated)
HOST_IP=                # Required for bridge networking (path analysis)
```

### Volume Mounts

#### Persistent Data
The `./data` directory contains:
- SQLite database (configs, audit results)
- Encrypted credentials
- Application state

**Backup:** Regular backups of `./data` directory recommended.

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
docker-compose logs -f network-optimizer
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
```bash
docker-compose ps
curl http://localhost:8042/api/health
```

Healthy output:
```
NAME                          STATUS
network-optimizer             Up (healthy)
```

## Troubleshooting

### Service Won't Start

**Check logs:**
```bash
docker-compose logs <service-name>
```

**Common issues:**
1. **Port conflicts:** Another service using 8042
   - Solution: Change `WEB_PORT` in `.env`
2. **Permission errors:** Cannot write to volumes
   - Solution: `chmod` the directories or check Docker volume permissions

### Reset Everything

**Complete reset (deletes all data):**
```bash
docker-compose down -v
rm -rf data/
docker-compose up -d
```

## Security Considerations

### Production Deployment

1. **Set a strong admin password** via Settings → Admin Password after first login
2. **Restrict network access:**
   - Use firewall rules to limit who can access port 8042
   - Consider reverse proxy with SSL (nginx, Caddy, Traefik)
3. **Enable HTTPS** with reverse proxy:
   ```nginx
   server {
       listen 443 ssl;
       server_name network-optimizer.example.com;

       ssl_certificate /path/to/cert.pem;
       ssl_certificate_key /path/to/key.pem;

       location / {
           proxy_pass http://localhost:8042;
           proxy_http_version 1.1;
           proxy_set_header Upgrade $http_upgrade;
           proxy_set_header Connection "upgrade";
       }
   }
   ```
4. **Backup regularly:** Back up the `./data` directory which contains the SQLite database and credentials.

## Upgrading

### Standard Updates

```bash
docker compose down
docker compose pull
docker compose up -d
```

Data persists in the `./data` volume.

### Migrating from Build-from-Source

If you've been building locally with `docker compose build` and want to switch to pre-built images, see the [Migration Guide](DEPLOYMENT.md#migrating-from-build-from-source-to-pre-built-images). A simple `docker compose pull` won't work because your locally-built image already has the registry tag.

### Before Major Updates

```bash
# Backup data first
tar czf backup-$(date +%Y%m%d).tar.gz data/
```

## Support

For issues, feature requests, or questions:
- GitHub: https://github.com/Ozark-Connect/NetworkOptimizer
- Documentation: See `docs/` folder in repository

## License

Business Source License 1.1. See [LICENSE](../LICENSE) in the repository root.

© 2026 Ozark Connect
