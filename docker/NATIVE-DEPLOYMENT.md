# Native Deployment Guide

Run Network Optimizer directly on the host without Docker for maximum network performance.

## When to Use Native Deployment

**Recommended for:**
- **macOS/Windows users** - Docker Desktop adds virtualization overhead that can limit network throughput
- **Speed test accuracy** - Native deployment provides accurate multi-gigabit measurements
- **Low-overhead systems** - Minimal resource usage without container overhead
- **Dedicated appliances** - Purpose-built network monitoring devices

**Use Docker instead if:**
- You prefer containerized deployments
- You need easy updates via image pulls
- Your network speeds are under 2 Gbps (except macOS - see below)

**macOS note:** Docker Desktop limits network throughput for speed testing. For accurate multi-gigabit measurements on macOS, use native deployment. The native install script includes OpenSpeedTest setup, so you get both maximum performance and browser-based speed testing.

## Platform-Specific Instructions

- [macOS Deployment](#macos-deployment)
- [Linux Deployment](#linux-deployment)
- [Windows Deployment](#windows-deployment) - Use the Windows Installer instead

---

## macOS Deployment

For the quickest macOS installation, see [macOS Installation Guide](../docs/MACOS-INSTALLATION.md).

For manual installation or customization, continue with the steps below.

---

### Manual Installation

### Prerequisites

**System Requirements:**
- macOS 11 (Big Sur) or later
- Intel or Apple Silicon (M1/M2/M3)
- 2GB RAM minimum
- 1GB disk space

**Required Software:**
```bash
# Install Homebrew if not present
/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"

# Install required tools
brew install sshpass iperf3
```

### Build from Source

```bash
# Install .NET SDK (if not present)
brew install dotnet

# Clone repository
git clone https://github.com/Ozark-Connect/NetworkOptimizer.git
# or via SSH: git clone git@github.com:Ozark-Connect/NetworkOptimizer.git
cd NetworkOptimizer

# Build for your architecture
# Apple Silicon (M1/M2/M3):
dotnet publish src/NetworkOptimizer.Web -c Release -r osx-arm64 --self-contained -o ~/network-optimizer

# Intel Macs:
# dotnet publish src/NetworkOptimizer.Web -c Release -r osx-x64 --self-contained -o ~/network-optimizer

cd ~/network-optimizer
```

### Code Signing

macOS requires binaries to be signed. Sign with an ad-hoc signature:

```bash
cd ~/network-optimizer

# Sign all dynamic libraries
find . -name '*.dylib' -exec codesign --force --sign - {} \;

# Sign main executable
codesign --force --sign - NetworkOptimizer.Web

# Verify signature
codesign -v NetworkOptimizer.Web
```

### Create Startup Script

```bash
cat > ~/network-optimizer/start.sh << 'EOF'
#!/bin/bash
cd "$(dirname "$0")"

# Add Homebrew to PATH
export PATH="/opt/homebrew/bin:/usr/local/bin:$PATH"

# Environment configuration
export TZ="America/Chicago"  # Change to your timezone
export ASPNETCORE_URLS="http://0.0.0.0:8042"

# Host IP - required for iperf3 client result tracking
export HOST_IP="192.168.1.100"  # Change to this Mac's IP address

# Enable iperf3 server for client speed testing (port 5201)
export Iperf3Server__Enabled=true

# Optional: Set admin password (otherwise auto-generated on first run)
# export APP_PASSWORD="your-secure-password"

# Start the application
./NetworkOptimizer.Web
EOF

chmod +x ~/network-optimizer/start.sh
```

### Create Log Directory

```bash
mkdir -p ~/network-optimizer/logs
```

### Install as System Service (launchd)

Create the service definition:

```bash
cat > ~/Library/LaunchAgents/com.networkoptimizer.app.plist << 'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>com.networkoptimizer.app</string>
    <key>ProgramArguments</key>
    <array>
        <string>/Users/YOUR_USERNAME/network-optimizer/start.sh</string>
    </array>
    <key>WorkingDirectory</key>
    <string>/Users/YOUR_USERNAME/network-optimizer</string>
    <key>KeepAlive</key>
    <true/>
    <key>RunAtLoad</key>
    <true/>
    <key>StandardOutPath</key>
    <string>/Users/YOUR_USERNAME/network-optimizer/logs/stdout.log</string>
    <key>StandardErrorPath</key>
    <string>/Users/YOUR_USERNAME/network-optimizer/logs/stderr.log</string>
</dict>
</plist>
EOF
```

**Important:** Replace `YOUR_USERNAME` with your actual username:

```bash
sed -i '' "s/YOUR_USERNAME/$(whoami)/g" ~/Library/LaunchAgents/com.networkoptimizer.app.plist
```

### Start the Service

```bash
# Load and start the service
launchctl load ~/Library/LaunchAgents/com.networkoptimizer.app.plist

# Verify it's running
launchctl list | grep networkoptimizer

# Check health
curl -s http://localhost:8042/api/health
```

### Access the Application

Open your browser to: **http://localhost:8042**

On first run, check the logs for the auto-generated admin password:
```bash
grep -A5 "AUTO-GENERATED" ~/network-optimizer/logs/stdout.log
```

### Service Management

```bash
# Stop service
launchctl unload ~/Library/LaunchAgents/com.networkoptimizer.app.plist

# Start service
launchctl load ~/Library/LaunchAgents/com.networkoptimizer.app.plist

# Restart service
launchctl unload ~/Library/LaunchAgents/com.networkoptimizer.app.plist && \
launchctl load ~/Library/LaunchAgents/com.networkoptimizer.app.plist

# View logs
tail -f ~/network-optimizer/logs/stdout.log

# Check status
launchctl list | grep networkoptimizer && curl -s http://localhost:8042/api/health
```

### Data Location

Network Optimizer stores data in:
- **Database:** `~/Library/Application Support/NetworkOptimizer/network_optimizer.db`
- **Credentials:** `~/Library/Application Support/NetworkOptimizer/.credential_key`
- **Logs:** `~/network-optimizer/logs/`

### Updating

```bash
# Stop service
launchctl unload ~/Library/LaunchAgents/com.networkoptimizer.app.plist

# Backup database (optional)
cp ~/Library/Application\ Support/NetworkOptimizer/network_optimizer.db ~/network_optimizer.db.backup

# Pull latest from main and rebuild
cd ~/NetworkOptimizer
git fetch origin && git checkout main && git pull
dotnet publish src/NetworkOptimizer.Web -c Release -r osx-arm64 --self-contained -o ~/network-optimizer

# Re-sign binaries
cd ~/network-optimizer
find . -name '*.dylib' -exec codesign --force --sign - {} \;
codesign --force --sign - NetworkOptimizer.Web

# Start service
launchctl load ~/Library/LaunchAgents/com.networkoptimizer.app.plist
```

### Uninstall

```bash
# Stop and remove service
launchctl unload ~/Library/LaunchAgents/com.networkoptimizer.app.plist
rm ~/Library/LaunchAgents/com.networkoptimizer.app.plist

# Remove application
rm -rf ~/network-optimizer

# Remove data (optional - keeps your settings if you reinstall)
rm -rf ~/Library/Application\ Support/NetworkOptimizer
```

---

## Linux Deployment

### Prerequisites

**System Requirements:**
- Ubuntu 20.04+, Debian 11+, RHEL 8+, or compatible
- x64 or ARM64 architecture
- 2GB RAM minimum
- 1GB disk space

**Required Software:**
```bash
# Debian/Ubuntu
sudo apt update
sudo apt install -y sshpass iperf3

# RHEL/CentOS/Fedora
sudo dnf install -y epel-release
sudo dnf install -y sshpass iperf3
```

### Build from Source

```bash
# Install .NET SDK
# Debian/Ubuntu:
wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 10.0
export PATH="$HOME/.dotnet:$PATH"

# Clone and build
git clone https://github.com/Ozark-Connect/NetworkOptimizer.git
# or via SSH: git clone git@github.com:Ozark-Connect/NetworkOptimizer.git
cd NetworkOptimizer

# Create installation directory
sudo mkdir -p /opt/network-optimizer
sudo chown $USER:$USER /opt/network-optimizer

# Build for your architecture (x64)
dotnet publish src/NetworkOptimizer.Web -c Release -r linux-x64 --self-contained -o /opt/network-optimizer

# For ARM64, use:
# dotnet publish src/NetworkOptimizer.Web -c Release -r linux-arm64 --self-contained -o /opt/network-optimizer

# Make executable
chmod +x /opt/network-optimizer/NetworkOptimizer.Web
```

### Create Startup Script

```bash
cat > /opt/network-optimizer/start.sh << 'EOF'
#!/bin/bash
cd "$(dirname "$0")"

# Environment configuration
export TZ="America/Chicago"  # Change to your timezone
export ASPNETCORE_URLS="http://0.0.0.0:8042"

# Host IP - required for iperf3 client result tracking
export HOST_IP="192.168.1.100"  # Change to this server's IP address

# Enable iperf3 server for client speed testing (port 5201)
export Iperf3Server__Enabled=true

# Optional: Set admin password
# export APP_PASSWORD="your-secure-password"

# Start the application
./NetworkOptimizer.Web
EOF

chmod +x /opt/network-optimizer/start.sh
```

### Install as System Service (systemd)

```bash
sudo cat > /etc/systemd/system/network-optimizer.service << 'EOF'
[Unit]
Description=Network Optimizer
After=network.target

[Service]
Type=simple
User=YOUR_USERNAME
WorkingDirectory=/opt/network-optimizer
ExecStart=/opt/network-optimizer/start.sh
Restart=always
RestartSec=10
StandardOutput=append:/opt/network-optimizer/logs/stdout.log
StandardError=append:/opt/network-optimizer/logs/stderr.log

[Install]
WantedBy=multi-user.target
EOF

# Replace YOUR_USERNAME
sudo sed -i "s/YOUR_USERNAME/$USER/g" /etc/systemd/system/network-optimizer.service

# Create log directory
mkdir -p /opt/network-optimizer/logs

# Enable and start
sudo systemctl daemon-reload
sudo systemctl enable network-optimizer
sudo systemctl start network-optimizer
```

### Service Management

```bash
# Check status
sudo systemctl status network-optimizer

# Stop
sudo systemctl stop network-optimizer

# Start
sudo systemctl start network-optimizer

# Restart
sudo systemctl restart network-optimizer

# View logs
tail -f /opt/network-optimizer/logs/stdout.log
journalctl -u network-optimizer -f
```

### Data Location

- **Database:** `~/.local/share/NetworkOptimizer/network_optimizer.db`
- **Credentials:** `~/.local/share/NetworkOptimizer/.credential_key`
- **Logs:** `/opt/network-optimizer/logs/`

---

## Windows Deployment

**Use the Windows Installer instead of manual deployment.**

Download the MSI installer from [GitHub Releases](https://github.com/Ozark-Connect/NetworkOptimizer/releases). The installer provides:

- One-click installation
- Automatic Windows Service setup (starts at boot)
- Bundled iperf3 for speed testing
- Proper uninstall via Windows Settings

After installation, access the web UI at **http://localhost:8042** (or use the machine's IP/hostname from other devices).

---

## Client Speed Testing

Native deployments support both browser-based and CLI-based client speed testing.

### OpenSpeedTest™ (Browser-Based)

The macOS install script (`scripts/install-macos-native.sh`) automatically sets up OpenSpeedTest with nginx, providing browser-based speed testing from any device - no client software required.

After installation, access SpeedTest at: **http://your-mac-ip:3005**

For manual setup or Linux, see [Manual OpenSpeedTest Setup](#manual-openspeedtest-setup) below.

### iperf3 Server Mode

For CLI-based testing with iperf3 clients.

### Enable iperf3 Server Mode

Add to your startup script:
```bash
export Iperf3Server__Enabled=true
```

### Port Conflicts

If you already have an iperf3 server running:
```bash
# Linux - stop existing service
sudo systemctl stop iperf3

# Check if port 5201 is in use
sudo ss -tlnp | grep 5201
```

### Testing from Clients

From any device with iperf3 installed:
```bash
# Download test
iperf3 -c your-server-ip

# Upload test
iperf3 -c your-server-ip -R
```

Results appear in Network Optimizer's Client Speed Test page.

### Manual OpenSpeedTest Setup

If you installed manually (without the install script), you can set up OpenSpeedTest:

**macOS:**
```bash
# Install nginx
brew install nginx

# Create SpeedTest directory
mkdir -p ~/network-optimizer/SpeedTest/{conf,logs,temp,html/assets/{css,js,fonts,images/icons}}
cd ~/network-optimizer/SpeedTest

# Copy files from repo (adjust path as needed)
REPO=~/NetworkOptimizer
cp $REPO/src/NetworkOptimizer.Installer/SpeedTest/nginx.conf conf/
cp $REPO/src/NetworkOptimizer.Installer/SpeedTest/nginx/conf/mime.types conf/
cp $REPO/src/OpenSpeedTest/{index.html,hosted.html,downloading,upload} html/
cp -r $REPO/src/OpenSpeedTest/assets/* html/assets/

# Create config.js with your server's IP
cat > html/assets/js/config.js << 'EOF'
window.NETWORK_OPTIMIZER_CONFIG = {
    resultsApiUrl: "http://YOUR_IP:8042/api/public/speedtest/results"
};
EOF

# Start nginx
nginx -c ~/network-optimizer/SpeedTest/conf/nginx.conf -p ~/network-optimizer/SpeedTest
```

**Linux:**
```bash
# Install nginx
sudo apt install nginx  # Debian/Ubuntu
# or
sudo dnf install nginx  # RHEL/Fedora

# Create SpeedTest directory
sudo mkdir -p /opt/network-optimizer/SpeedTest/{conf,logs,temp,html/assets/{css,js,fonts,images/icons}}
sudo chown -R $USER: /opt/network-optimizer/SpeedTest

# Copy files from repo and create config.js (same as macOS, adjust paths)
# Start nginx with the SpeedTest config
sudo nginx -c /opt/network-optimizer/SpeedTest/conf/nginx.conf -p /opt/network-optimizer/SpeedTest
```

Access SpeedTest at `http://your-server:3005`. Results automatically appear in Network Optimizer.

## Firewall Configuration

Ensure port 8042 (or your configured port) is accessible:

**macOS:**
```bash
# Usually not needed for local access
# For remote access, allow in System Preferences > Security & Privacy > Firewall
```

**Linux (UFW):**
```bash
sudo ufw allow 8042/tcp
```

**Linux (firewalld):**
```bash
sudo firewall-cmd --permanent --add-port=8042/tcp
sudo firewall-cmd --reload
```

**Windows:**
```powershell
netsh advfirewall firewall add rule name="Network Optimizer" dir=in action=allow protocol=tcp localport=8042
```

---

## Reverse Proxy (Optional)

For HTTPS access, place behind a reverse proxy like Caddy, nginx, or Traefik.

### Caddy Example

```caddy
network-optimizer.example.com {
    reverse_proxy localhost:8042
}
```

### nginx Example

```nginx
server {
    listen 443 ssl http2;
    server_name network-optimizer.example.com;

    ssl_certificate /path/to/cert.pem;
    ssl_certificate_key /path/to/key.pem;

    location / {
        proxy_pass http://localhost:8042;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

---

## Troubleshooting

### macOS: "Killed: 9" Error

The binary needs code signing:
```bash
find ~/network-optimizer -name '*.dylib' -exec codesign --force --sign - {} \;
codesign --force --sign - ~/network-optimizer/NetworkOptimizer.Web
```

### macOS: sshpass/iperf3 Not Found

Add Homebrew to PATH in `start.sh`:
```bash
export PATH="/opt/homebrew/bin:/usr/local/bin:$PATH"
```

### Linux: Permission Denied

```bash
chmod +x /opt/network-optimizer/NetworkOptimizer.Web
chmod +x /opt/network-optimizer/start.sh
```

### All Platforms: Port Already in Use

Change the port in your startup script:
```bash
export ASPNETCORE_URLS="http://0.0.0.0:8080"  # Use different port
```

### Check Application Logs

```bash
# macOS
tail -f ~/network-optimizer/logs/stdout.log

# Linux
tail -f /opt/network-optimizer/logs/stdout.log
journalctl -u network-optimizer -f

# Windows
type C:\NetworkOptimizer\logs\stdout.log
```

---

## Support

- Documentation: See `docs/` folder in repository
- GitHub Issues: https://github.com/Ozark-Connect/NetworkOptimizer/issues
- Email: tj@ozarkconnect.net
