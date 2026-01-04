# Deployment Guide

Production deployment guide for Network Optimizer.

## Deployment Options

| Option | Best For | Guide |
|--------|----------|-------|
| Linux + Docker | Self-built servers, VMs, cloud | [Below](#1-linux-docker-recommended) |
| NAS + Docker | Synology, QNAP, Unraid | [NAS Deployment](#2-nas-deployment-docker) |
| macOS Native | Mac servers, multi-gigabit speed testing | [Native Guide](NATIVE-DEPLOYMENT.md#macos-deployment) |
| Linux Native | Maximum performance, no Docker | [Native Guide](NATIVE-DEPLOYMENT.md#linux-deployment) |
| Windows | Windows servers | [Native Guide](NATIVE-DEPLOYMENT.md#windows-deployment) |

---

### 1. Linux + Docker (Recommended)

Deploy on any Linux server using Docker Compose. This is the recommended approach for self-built NAS, home servers, VMs, and cloud instances.

**Requirements:**
- Docker 20.10+ and Docker Compose 2.0+
- 2GB RAM minimum (4GB recommended)
- 10GB disk space
- Ubuntu 20.04+, Debian 11+, RHEL/CentOS 8+, or compatible

#### Quick Start

```bash
# Install Docker (if not already installed)
curl -fsSL https://get.docker.com | sh
sudo usermod -aG docker $USER
# Log out and back in for group changes

# Clone repository
git clone https://github.com/Ozark-Connect/NetworkOptimizer.git
cd network-optimizer/docker

# Configure environment (optional - defaults work out of the box)
cp .env.example .env
nano .env  # Set timezone and other options

# Build and start with host networking (recommended for Linux)
docker compose build
docker compose up -d

# Check logs for the auto-generated admin password
docker compose logs network-optimizer | grep -A5 "FIRST-RUN"

# Verify health
docker compose ps
curl http://localhost:8042/api/health
```

Access at: **http://your-server:8042**

#### Network Mode Options

**Host Networking (Recommended for Linux):**
```yaml
# docker-compose.yml uses network_mode: host by default
# This provides best performance and accurate IP detection
```

**Bridge Networking (if host mode unavailable):**
```bash
# Use docker-compose.macos.yml which uses port mapping
# IMPORTANT: Set HOST_IP in .env to your server's IP for accurate path analysis
docker compose -f docker-compose.macos.yml up -d
```

#### Service Management

```bash
# View logs
docker compose logs -f

# Restart
docker compose restart

# Stop
docker compose down

# Update to latest
docker compose pull
docker compose up -d

# Full rebuild (after Dockerfile changes)
docker compose build --no-cache
docker compose up -d
```

#### Systemd Integration (Auto-Start on Boot)

```bash
# Enable Docker to start on boot
sudo systemctl enable docker

# Docker Compose containers with restart: unless-stopped will auto-start
```

Or create a dedicated systemd service:

```bash
sudo cat > /etc/systemd/system/network-optimizer.service << 'EOF'
[Unit]
Description=Network Optimizer
Requires=docker.service
After=docker.service

[Service]
Type=oneshot
RemainAfterExit=yes
WorkingDirectory=/opt/network-optimizer/docker
ExecStart=/usr/bin/docker compose up -d
ExecStop=/usr/bin/docker compose down
TimeoutStartSec=0

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable network-optimizer
```

---

### 2. NAS Deployment (Docker)

For commercial NAS devices with container support.

#### Synology NAS

1. Install Container Manager from Package Center
2. Clone or upload the repository to `/docker/network-optimizer`
3. Copy `.env.example` to `.env` and configure
4. Create project in Container Manager pointing to docker-compose.yml
5. Start containers

**Note:** If using bridge networking, set `HOST_IP` in `.env` to your NAS IP address.

#### QNAP NAS

1. Install Container Station
2. Create shared folders
3. Import `docker-compose.yml`
4. Configure environment variables
5. Deploy stack

#### Unraid

1. Install Community Applications plugin
2. Search for "Network Optimizer" (when published)
3. Or use manual Docker Compose deployment

### 4. Native Deployment (No Docker)

For maximum network performance or systems without Docker, run natively on the host.

**Best for:**
- macOS systems (avoids Docker Desktop's ~1.8 Gbps network throughput limitation)
- Systems where Docker overhead is undesirable
- Dedicated appliances

**Supported Platforms:**
- macOS 11+ (Intel or Apple Silicon)
- Linux (Ubuntu 20.04+, Debian 11+, RHEL 8+)
- Windows Server 2019+ / Windows 10+

See [Native Deployment Guide](NATIVE-DEPLOYMENT.md) for detailed instructions.

## Pre-Deployment Checklist

- [ ] Docker and Docker Compose installed
- [ ] Sufficient disk space (10GB minimum)
- [ ] Network access to UniFi Controller
- [ ] Firewall rules configured (if applicable)
- [ ] `.env` file configured with secure passwords
- [ ] SSL certificates ready (if using HTTPS)
- [ ] SSH enabled on UniFi devices (required for SQM and LAN speed testing, see below)

## Installation Steps

### 1. Download Files

```bash
git clone https://github.com/Ozark-Connect/NetworkOptimizer.git
cd NetworkOptimizer/docker
```

### 2. Configure Environment

```bash
# Copy template
cp .env.example .env

# Edit with your settings
nano .env
```

**Recommended changes:**
```env
# Set your timezone
TZ=America/Chicago
```

**Admin Password:**

On first run, an auto-generated password is displayed in the logs. After logging in,
go to **Settings > Admin Password** to set your own password (recommended).

Password precedence: Database (Settings UI) > `APP_PASSWORD` env var > Auto-generated

Optionally, set `APP_PASSWORD` in `.env` if you want to configure a password before first login.

### 3. Deploy Stack

```bash
docker-compose up -d
```

### 4. Verify Deployment

```bash
# Check service health
docker-compose ps

# View logs
docker-compose logs -f

# Test health endpoint
curl http://localhost:8042/api/health
```

Expected output:
```
NAME                          STATUS
network-optimizer             Up (healthy)
```

### 5. Access Web UI

- Web UI: http://your-server:8042

## Production Configuration

### HTTPS with Reverse Proxy

Use nginx, Caddy, or Traefik for SSL termination.

**If the reverse proxy is on the same host**, add to your `.env`:
```env
BIND_LOCALHOST_ONLY=true
```
This binds the app to `127.0.0.1:8042` instead of all interfaces, so only the local proxy can access it.

#### Nginx Example

```nginx
# /etc/nginx/sites-available/network-optimizer
server {
    listen 80;
    server_name network-optimizer.example.com;
    return 301 https://$server_name$request_uri;
}

server {
    listen 443 ssl http2;
    server_name network-optimizer.example.com;

    ssl_certificate /etc/letsencrypt/live/network-optimizer.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/network-optimizer.example.com/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_ciphers HIGH:!aNULL:!MD5;

    # Blazor Web UI
    location / {
        proxy_pass http://localhost:8042;
        proxy_http_version 1.1;

        # WebSocket support for Blazor
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";

        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;

        # Timeouts for long-running operations
        proxy_connect_timeout 60s;
        proxy_send_timeout 60s;
        proxy_read_timeout 60s;
    }
}
```

Enable and restart:
```bash
sudo ln -s /etc/nginx/sites-available/network-optimizer /etc/nginx/sites-enabled/
sudo nginx -t
sudo systemctl reload nginx
```

#### Caddy Example (Automatic HTTPS)

```caddy
# /etc/caddy/Caddyfile
network-optimizer.example.com {
    reverse_proxy localhost:8042
}
```

Restart Caddy:
```bash
sudo systemctl reload caddy
```

### Firewall Configuration

#### UFW (Ubuntu/Debian)

```bash
# Allow SSH
sudo ufw allow 22/tcp

# Allow HTTP/HTTPS (if using reverse proxy)
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp

# Or allow direct access to the web UI
sudo ufw allow 8042/tcp  # Web UI

sudo ufw enable
```

#### firewalld (RHEL/CentOS)

```bash
sudo firewall-cmd --permanent --add-service=http
sudo firewall-cmd --permanent --add-service=https
sudo firewall-cmd --permanent --add-port=8042/tcp
sudo firewall-cmd --reload
```

### Backup Strategy

#### Automated Backups

Create backup script:
```bash
#!/bin/bash
# /usr/local/bin/backup-network-optimizer.sh

BACKUP_DIR=/backups/network-optimizer
DATE=$(date +%Y%m%d-%H%M%S)

# Create backup directory
mkdir -p $BACKUP_DIR

# Backup SQLite data and configuration
tar czf $BACKUP_DIR/data-$DATE.tar.gz -C /path/to/docker data/

# Cleanup old backups (keep last 7 days)
find $BACKUP_DIR -type f -mtime +7 -delete

echo "Backup completed: $DATE"
```

Add to crontab:
```bash
# Daily backup at 2 AM
0 2 * * * /usr/local/bin/backup-network-optimizer.sh >> /var/log/network-optimizer-backup.log 2>&1
```

#### Restore from Backup

```bash
# Stop services
docker-compose down

# Restore data
tar xzf /backups/network-optimizer/data-20240101-020000.tar.gz -C /path/to/docker/

# Start services
docker-compose up -d
```

### Monitoring and Alerting

#### System Monitoring

Use Docker healthchecks:
```bash
# Check all services
watch docker-compose ps

# Monitor resource usage
docker stats
```

#### Log Monitoring

Centralized logging with rsyslog or similar:
```yaml
# docker-compose.yml addition
logging:
  driver: syslog
  options:
    syslog-address: "udp://your-syslog-server:514"
    tag: "network-optimizer"
```

#### Uptime Monitoring

Use external monitoring:
- UptimeRobot
- Healthchecks.io
- Self-hosted Uptime Kuma

Configure health check endpoint:
```bash
# Monitor this endpoint
http://your-server:8042/api/health
```

### Resource Limits

Add resource constraints for production:

```yaml
# docker-compose.override.yml
services:
  network-optimizer:
    deploy:
      resources:
        limits:
          cpus: '2.0'
          memory: 2G
        reservations:
          cpus: '1.0'
          memory: 1G
    restart: always
```

Apply with:
```bash
docker-compose up -d
```

### Logging Configuration

Control log verbosity via environment variables in `.env`:

```env
# General framework logging (Microsoft, EF Core, ASP.NET, etc.)
LOG_LEVEL=Information

# Network Optimizer application logging
APP_LOG_LEVEL=Debug
```

**Log Levels (least to most verbose):** Critical, Error, Warning, Information, Debug, Trace

**Common configurations:**

| Scenario | LOG_LEVEL | APP_LOG_LEVEL |
|----------|-----------|---------------|
| Production (default) | Information | Information |
| Debugging app issues | Information | Debug |
| Full diagnostics | Debug | Debug |

After changing `.env`, recreate the container to apply:
```bash
docker compose down && docker compose up -d
```

**Note:** `docker compose restart` does NOT reload environment variables. You must recreate the container.

View logs:
```bash
# Follow logs
docker compose logs -f network-optimizer

# Last 100 lines
docker compose logs --tail=100 network-optimizer
```

## Upgrade Procedure

Currently, Network Optimizer is deployed by building from source. Pre-built images will be published in a future release.

```bash
# Pull latest source
cd /path/to/network-optimizer
git pull

# Rebuild and restart
cd docker
docker compose build
docker compose up -d

# Verify
docker compose ps
docker compose logs -f
```

For significant updates (major version changes or Dockerfile modifications), use a full rebuild:

```bash
docker compose build --no-cache
docker compose up -d
```

## Troubleshooting

### Container Won't Start

```bash
# Check logs for errors
docker compose logs network-optimizer

# Common issues:
# - Port 8042 already in use: stop conflicting service or change port
# - Permission denied on data directory: check ownership of mounted volumes
# - Out of disk space: df -h
```

### Can't Connect to UniFi Controller

1. Verify the controller URL is correct (include https:// and port if non-standard)
2. Ensure you're using a **local admin account**, not Ubiquiti SSO
3. Check network connectivity: `curl -k https://your-controller:443`
4. For self-signed certificates, enable "Ignore SSL errors" in Settings

### SSH Connection Failures

```bash
# Test SSH manually from the container
docker exec -it network-optimizer ssh username@gateway-ip

# Common issues:
# - SSH not enabled on device (see UniFi SSH Configuration section)
# - Wrong credentials
# - Firewall blocking port 22
# - Host key verification (container may need to accept new host keys)
```

### Blazor UI Not Loading / Disconnects

Blazor Server uses WebSocket connections. If the UI shows "Reconnecting..." or won't load:

1. Check that your reverse proxy supports WebSockets (see nginx/Caddy examples above)
2. Ensure proxy timeouts are sufficient (60s+)
3. Check browser console for connection errors

### Database Issues

The SQLite database is stored in the `data/` volume. If you encounter database errors:

```bash
# Check database file exists and has correct permissions
docker exec network-optimizer ls -la /app/data/

# View recent application logs
docker compose logs --tail=100 network-optimizer
```

## Security Considerations

### Protect Your Credentials

The `.env` file and SQLite database contain sensitive information:

```bash
# Restrict .env file permissions
chmod 600 .env

# Data directory contains the database with stored credentials
chmod 700 data/
```

### Network Access

Network Optimizer stores UniFi controller credentials and SSH passwords. Limit access to the web UI:

- Use a reverse proxy with authentication if exposing beyond your local network
- Consider firewall rules to restrict access to trusted IPs
- Use HTTPS via reverse proxy (see examples above)

### UniFi Account

Create a dedicated local admin account on your UniFi controller for Network Optimizer rather than using your primary admin account. This allows you to revoke access without affecting other integrations.

## Support

- GitHub Issues: https://github.com/Ozark-Connect/NetworkOptimizer/issues
- Email: tj@ozarkconnect.net

## UniFi SSH Configuration

SSH access is **optional** for Security Audit but **required** for:
- **Adaptive SQM:** Deploying bandwidth management scripts to your gateway
- **LAN Speed Testing:** Running iperf3 tests between gateway and network devices

### Enabling SSH in UniFi

**Important:** Both SSH settings must be configured via the UniFi Network web interface. These options are not available in the iOS or Android UniFi apps.

#### Console SSH (UniFi Network 9.5+)

Enables SSH access to the controller/console itself (UCG, UDM, etc.):

1. Open **UniFi Network** in a web browser
2. Go to **Settings** (gear icon)
3. Navigate to **Control Plane** > **Console**
4. Find **SSH** and enable it
5. Set a password

#### Device SSH (UniFi Network 9.5+)

Enables SSH access to adopted devices (switches, access points):

1. Open **UniFi Network** in a web browser
2. Go to **UniFi Devices** in the left sidebar
3. Scroll to the bottom of the left-hand menu and click **Device Updates and Settings**
4. Select **Device SSH Settings**
5. Check **Device SSH Authentication**
6. Set username and password
7. Optionally add SSH keys (recommended for better security)

### Testing SSH Access

After enabling SSH, verify connectivity:

```bash
# Test gateway SSH
ssh <username>@<gateway-ip>

# You should get a shell prompt on the UniFi device
```

### Credentials in Network Optimizer

Once SSH is enabled in UniFi, enter the same credentials in Network Optimizer:
1. Go to **Settings** in Network Optimizer
2. Enter your SSH username and password
3. Click **Test SSH** to verify connectivity

For custom devices (non-UniFi equipment), you can configure per-device SSH credentials in the LAN Speed Testing section.

## Client Speed Testing (Optional)

Enable speed testing from any device on your LAN (phones, tablets, laptops, IoT devices) without requiring SSH access.

### Overview

Two methods are available:

| Method | Best For | Port |
|--------|----------|------|
| **OpenSpeedTest™** | Browser-based testing from any device | 3005 (configurable) |
| **iperf3 Server** | CLI testing with iperf3 clients | 5201 |

Results from both methods are stored in Network Optimizer and visible in the Client Speed Test page.

**Why separate containers?** OpenSpeedTest runs as its own container (not proxied through Network Optimizer) for performance reasons. Speed tests can push massive bandwidth (multi-gigabit to 100 Gbps on high-end networks), and routing that traffic through a reverse proxy or the .NET application would add overhead and reduce accuracy. The only data sent to Network Optimizer is the small JSON result payload after the test completes.

### OpenSpeedTest™ (Browser-Based)

Bundled as part of the Docker Compose stack. Access at `http://your-server:3005` (port configurable via `OPENSPEEDTEST_PORT`).

**Configuration:**

Set these environment variables in `.env`:

```env
# Server IP for path analysis (only needed if auto-detection fails, e.g., bridge networking)
HOST_IP=192.168.1.100

# Hostname for canonical URL enforcement and friendlier user-facing URLs
# Requires DNS resolution (can be local DNS via router/Pi-hole)
HOST_NAME=nas

# If app/API is behind a reverse proxy with HTTPS (takes priority for canonical URL)
# Only affects app/API URL, not the OpenSpeedTest container URL
REVERSE_PROXIED_HOST_NAME=optimizer.example.com
```

These settings enforce a canonical URL via 302 redirect. Priority: `REVERSE_PROXIED_HOST_NAME` > `HOST_NAME` > `HOST_IP`. If none set, any Host header is accepted. `HOST_IP` is only required for speed test path analysis when the server IP can't be auto-detected.

The API URL for result reporting is constructed using this priority:
1. `REVERSE_PROXIED_HOST_NAME` → `https://hostname/api/public/speedtest/results`
2. `HOST_NAME` → `http://hostname:8042/api/public/speedtest/results`
3. `HOST_IP` → `http://ip:8042/api/public/speedtest/results`

**Usage:**
1. Open `http://your-server:3005` (or your configured port) from any device on your network
2. Run the speed test
3. Results automatically appear in Network Optimizer's Client Speed Test page

### iperf3 Server Mode

Run iperf3 as a server inside the Network Optimizer container for CLI-based testing.

**Enable in `.env`:**
```env
IPERF3_SERVER_ENABLED=true
```

**Usage from client devices:**
```bash
# Upload test (client to server, 4 streams)
iperf3 -c your-server -P 4

# Download test (server to client, 4 streams)
iperf3 -c your-server -P 4 -R

# Bidirectional test (runs both directions simultaneously)
iperf3 -c your-server -P 4 --bidir
```

Results are captured automatically and stored with client IP identification.

### Port Conflicts

**Before enabling these features, check for existing services using the same ports:**

```bash
# Check for iperf3 server already running
sudo netstat -tlnp | grep 5201
# or
sudo ss -tlnp | grep 5201

# Check for existing services on port 3005
sudo netstat -tlnp | grep 3005
docker ps | grep -E "3000|3005"
```

**Common conflicts:**

| Port | Service | Resolution |
|------|---------|------------|
| 5201 | Existing iperf3 server | Stop: `sudo systemctl stop iperf3` |
| 3005 | OpenSpeedTest port conflict | Set `OPENSPEEDTEST_PORT=3006` (or another free port) in `.env` |

**Container name conflicts:**

The bundled OpenSpeedTest uses container name `openspeedtest`. If you have an existing container with this name:

```bash
# Remove existing container
docker stop openspeedtest && docker rm openspeedtest

# Then start the Network Optimizer stack
docker compose up -d
```

### Disabling Optional Services

To disable client speed testing components:

```env
# Disable iperf3 server (default)
IPERF3_SERVER_ENABLED=false

# To completely disable OpenSpeedTest, comment it out in docker-compose.yml
# or use a custom override file
```

## Next Steps

After deployment:
1. Access web UI and complete initial setup
2. Connect to UniFi Controller
3. Configure SSH access for gateway and devices (see above)
4. Run security audit
5. Configure SQM settings (if applicable)
6. Set up client speed testing (optional, see above)

See main documentation for feature guides.
