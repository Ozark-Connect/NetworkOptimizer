# Proxmox LXC Installation

Install Network Optimizer for UniFi in a Proxmox LXC container with a single command.

## Quick Start

Run this command on your **Proxmox VE host** (not inside a VM or container):

```bash
bash -c "$(wget -qLO - https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/proxmox/install.sh)"
```

Or with curl:

```bash
bash -c "$(curl -fsSL https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/proxmox/install.sh)"
```

The script will guide you through:
1. Container configuration (ID, hostname, resources, network)
2. Application settings (timezone, ports, optional HTTPS via Traefik, optional password)
3. Automatic installation of Docker and Network Optimizer
4. Optional Traefik HTTPS proxy with automatic Let's Encrypt certificates

## Requirements

- **Proxmox VE 7.0** or later
- **10GB** disk space minimum (20GB recommended)
- **2GB** RAM minimum (4GB recommended)
- Internet access for downloading container template and Docker images

## What Gets Installed

The script creates a privileged Debian LXC container (Debian 13 Trixie by default) with:

- Docker CE and Docker Compose (privileged container for reliable Docker operation)
- Network Optimizer (Blazor web UI on port 8042)
- OpenSpeedTest (browser-based speed testing on port 3005)
- Persistent storage in `/opt/network-optimizer/data`
- Auto-start on boot enabled
- Swap space for memory stability

## Default Configuration

| Setting | Default | Description |
|---------|---------|-------------|
| Container ID | Next available | Starting from 100, checks VMs too |
| Hostname | `network-optimizer` | Container hostname |
| Debian Version | 13 (Trixie) | Debian 12 (Bookworm) also supported |
| RAM | 2048 MB | Container memory |
| Swap | 512 MB | Swap space |
| CPU | 2 cores | Container CPU cores |
| Disk | 10 GB | Root filesystem size |
| Storage | `local-lvm` | Proxmox storage for container |
| Network | DHCP | Static IP also supported (with DNS) |
| SSH Access | Disabled | Enable for direct SSH root login |
| Web Port | 8042 | Network Optimizer web UI (fixed) |
| Speedtest Port | 3005 | OpenSpeedTest web UI (configurable) |
| iperf3 Server | Disabled | CLI-based speed testing (port 5201) |
| Host Redirect | Disabled | Redirect IP access to hostname (requires local DNS) |
| HTTPS (Traefik) | Disabled | Automatic HTTPS with Let's Encrypt via Cloudflare DNS |
| Reverse Proxy | None | Optional hostname for reverse proxy setup (skipped if Traefik enabled) |
| Geo Location | Disabled | GPS tagging for speed tests and signal levels (auto-enabled with Traefik) |
| Timezone | America/New_York | Container timezone |

## Post-Installation

### Get Admin Password

If you didn't set a password during installation, an auto-generated one is shown in the logs:

```bash
pct exec <CT_ID> -- docker logs network-optimizer 2>&1 | grep -A5 "AUTO-GENERATED"
```

### Access the Web UI

1. Open `http://<container-ip>:8042` in your browser
2. Log in with the admin password
3. Go to **Settings** and connect to your UniFi controller
4. Navigate to **Audit** to run your first security scan

### Container Management

```bash
# Enter container shell
pct enter <CT_ID>

# Start/stop container
pct start <CT_ID>
pct stop <CT_ID>

# View application logs
pct exec <CT_ID> -- docker logs -f network-optimizer

# Check container status
pct status <CT_ID>
```

### SSH Access (Optional)

If you enabled SSH during installation, set a root password:

```bash
pct exec <CT_ID> -- passwd
```

Then connect directly:

```bash
ssh root@<container-ip>
```

### Application Management

All commands run from the Proxmox host:

```bash
# View logs
pct exec <CT_ID> -- docker logs -f network-optimizer

# Restart services
pct exec <CT_ID> -- bash -c "cd /opt/network-optimizer && docker compose restart"

# Update to latest version
pct exec <CT_ID> -- bash -c "cd /opt/network-optimizer && docker compose pull && docker compose up -d"

# Check health
pct exec <CT_ID> -- curl -s http://localhost:8042/api/health
```

Or enter the container first:

```bash
pct enter <CT_ID>
cd /opt/network-optimizer
docker compose logs -f
docker compose pull && docker compose up -d
```

## HTTPS with Traefik

During installation, you can optionally enable HTTPS via a built-in [Traefik](https://github.com/Ozark-Connect/NetworkOptimizer-Proxy) reverse proxy. This provides:

- Automatic Let's Encrypt certificates via Cloudflare DNS-01 challenge
- HTTP/1.1 for speed tests (HTTP/2 multiplexing skews results), HTTP/2 for the main app
- Geo location tagging for speed tests and signal walk tests (browsers require HTTPS for location access)

**Requirements:**
- A domain managed by Cloudflare (for DNS-01 certificate validation)
- A Cloudflare API token with Zone > DNS > Edit permission ([create one here](https://dash.cloudflare.com/profile/api-tokens))
- Two DNS records pointing to your container's IP (e.g., `optimizer.example.com` and `speedtest.example.com`)

**What gets deployed:**
- Traefik container at `/opt/network-optimizer-proxy/`
- Dynamic configuration in `/opt/network-optimizer-proxy/dynamic/config.yml`
- Certificates stored in `/opt/network-optimizer-proxy/acme/acme.json`

**Management commands:**

```bash
# View Traefik logs
pct exec <CT_ID> -- docker logs -f traefik-proxy

# Update Traefik
pct exec <CT_ID> -- bash -c "cd /opt/network-optimizer-proxy && docker compose pull && docker compose up -d"

# Edit proxy configuration
pct exec <CT_ID> -- nano /opt/network-optimizer-proxy/dynamic/config.yml

# Edit proxy environment (ACME email, API token)
pct exec <CT_ID> -- nano /opt/network-optimizer-proxy/.env
```

**Note:** Certificates may take about a minute to issue on first start. If you see certificate errors immediately after installation, wait a moment and refresh.

If you don't enable Traefik during installation, you can still set up a reverse proxy manually later. See the [Deployment Guide](../../docker/DEPLOYMENT.md#https-with-reverse-proxy) for nginx, Caddy, and Traefik examples.

## Advanced Configuration

### Static IP Address

During installation, when prompted for IP address, enter a CIDR notation:

```
IP address [dhcp]: 192.168.1.100/24
Gateway IP: 192.168.1.1
```

### Custom Ports

Modify ports during installation or edit `/opt/network-optimizer/.env` afterward:

```bash
pct exec <CT_ID> -- nano /opt/network-optimizer/.env
```

Then restart:

```bash
pct exec <CT_ID> -- bash -c "cd /opt/network-optimizer && docker compose down && docker compose up -d"
```

### Resource Adjustments

Adjust container resources via Proxmox:

```bash
# Increase RAM to 4GB
pct set <CT_ID> --memory 4096

# Increase CPU to 4 cores
pct set <CT_ID> --cores 4

# Resize disk to 20GB
pct resize <CT_ID> rootfs 20G
```

### Enable iperf3 Server

For CLI-based speed testing from network devices:

```bash
pct exec <CT_ID> -- bash -c "echo 'IPERF3_SERVER_ENABLED=true' >> /opt/network-optimizer/.env"
pct exec <CT_ID> -- bash -c "cd /opt/network-optimizer && docker compose down && docker compose up -d"
```

## Network Configuration

### Host Networking

The container uses Docker's host networking mode by default, which provides:
- Best performance for speed testing
- Accurate client IP detection
- No port mapping overhead

### Firewall Rules

If you have Proxmox firewall enabled, allow these ports:

```bash
# Web UI
pct set <CT_ID> --firewall 1
pvesh create /nodes/<node>/lxc/<CT_ID>/firewall/rules --type in --action ACCEPT --dport 8042 --proto tcp

# OpenSpeedTest
pvesh create /nodes/<node>/lxc/<CT_ID>/firewall/rules --type in --action ACCEPT --dport 3005 --proto tcp

# iperf3 (if enabled)
pvesh create /nodes/<node>/lxc/<CT_ID>/firewall/rules --type in --action ACCEPT --dport 5201 --proto tcp
```

Or disable container firewall:

```bash
pct set <CT_ID> --firewall 0
```

## Backup and Restore

### Backup Container

```bash
# Full container backup
vzdump <CT_ID> --storage local --compress zstd --mode snapshot

# Or just the data directory
pct exec <CT_ID> -- tar czf /tmp/data-backup.tar.gz -C /opt/network-optimizer data
pct pull <CT_ID> /tmp/data-backup.tar.gz ./data-backup.tar.gz
```

### Restore Data

```bash
# Stop services
pct exec <CT_ID> -- bash -c "cd /opt/network-optimizer && docker compose down"

# Restore data
pct push <CT_ID> ./data-backup.tar.gz /tmp/data-backup.tar.gz
pct exec <CT_ID> -- tar xzf /tmp/data-backup.tar.gz -C /opt/network-optimizer

# Start services
pct exec <CT_ID> -- bash -c "cd /opt/network-optimizer && docker compose up -d"
```

## Troubleshooting

### Container Won't Start

Check for Docker-related issues:

```bash
# View container config
cat /etc/pve/lxc/<CT_ID>.conf

# Ensure nesting is enabled
pct set <CT_ID> --features nesting=1
```

### Docker Fails Inside Container

The script creates a privileged container with nesting enabled, which is the most reliable configuration for Docker. If Docker still fails:

```bash
# Check Docker service status
pct exec <CT_ID> -- systemctl status docker

# Try restarting Docker
pct exec <CT_ID> -- systemctl restart docker

# Check for errors in Docker logs
pct exec <CT_ID> -- journalctl -u docker --no-pager -n 50
```

### Application Not Responding

```bash
# Check Docker status
pct exec <CT_ID> -- systemctl status docker

# Check container logs
pct exec <CT_ID> -- docker logs network-optimizer

# Restart everything
pct exec <CT_ID> -- bash -c "cd /opt/network-optimizer && docker compose down && docker compose up -d"
```

### Permission Errors

If you see permission errors with volumes:

```bash
# Check ownership
pct exec <CT_ID> -- ls -la /opt/network-optimizer/

# Fix permissions
pct exec <CT_ID> -- chown -R 1000:1000 /opt/network-optimizer/data
```

### Reset Admin Password

If you forget the admin password:

```bash
pct exec <CT_ID> -- bash -c "curl -fsSL https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/reset-password.sh | bash -s -- --force"
```

**Manual fallback:**

```bash
# Clear the password
pct exec <CT_ID> -- docker exec network-optimizer sqlite3 /app/data/network_optimizer.db \
    "UPDATE AdminSettings SET Password = NULL, Enabled = 0;"

# Restart and view the new password
pct exec <CT_ID> -- docker restart network-optimizer
sleep 10
pct exec <CT_ID> -- docker logs network-optimizer 2>&1 | grep -A5 "AUTO-GENERATED"
```

## Uninstall

To completely remove Network Optimizer:

```bash
# Stop and destroy container
pct stop <CT_ID>
pct destroy <CT_ID>
```

## Manual Installation

If you prefer manual installation or the script doesn't work in your environment:

1. Create an LXC container (Debian 12, **privileged**, nesting enabled)
   ```bash
   pct create <CT_ID> <storage>:vztmpl/debian-12-standard_*.tar.zst \
       --hostname network-optimizer \
       --memory 2048 --swap 512 --cores 2 \
       --rootfs <storage>:10 \
       --net0 name=eth0,bridge=vmbr0,ip=dhcp \
       --unprivileged 0 --features nesting=1 --onboot 1
   ```
2. Start container and install Docker: https://docs.docker.com/engine/install/debian/
3. Follow the [Docker deployment guide](../../docker/DEPLOYMENT.md)

## Support

- **Issues:** [GitHub Issues](https://github.com/Ozark-Connect/NetworkOptimizer/issues)
- **Documentation:** [Deployment Guide](../../docker/DEPLOYMENT.md)

When reporting issues, include:
- Proxmox VE version (`pveversion`)
- Container logs (`pct exec <CT_ID> -- docker logs network-optimizer`)
- Any error messages from the installation script
