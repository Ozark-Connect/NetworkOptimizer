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
- You want bundled OpenSpeedTest for browser-based client speed testing
- Your network speeds are under 2 Gbps (except macOS - see below)

**macOS note:** Docker Desktop's virtualization limits network throughput to ~1.8 Gbps. If you need accurate multi-gigabit speed tests, use native deployment. If browser-based client speed testing (OpenSpeedTest) is more important than raw speed test accuracy, Docker may be acceptable.

## Platform-Specific Instructions

- [macOS Deployment](#macos-deployment)
- [Linux Deployment](#linux-deployment)
- [Windows Deployment](#windows-deployment)

---

## macOS Deployment

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
grep -i password ~/network-optimizer/logs/stdout.log
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

# Pull latest and rebuild
cd ~/NetworkOptimizer
git pull
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

### Prerequisites

**System Requirements:**
- Windows 10/11 or Windows Server 2019+
- x64 architecture
- 2GB RAM minimum
- 1GB disk space

**Required Software:**

Install from [winget](https://learn.microsoft.com/en-us/windows/package-manager/winget/) or download manually:

```powershell
# Using winget (recommended)
winget install iperf3
winget install sshpass  # May need manual installation
```

Or download directly:
- iperf3: https://iperf.fr/iperf-download.php
- sshpass: Build from source or use Cygwin

### Build from Source

```powershell
# Install .NET SDK from https://dotnet.microsoft.com/download/dotnet/10.0
# Or via winget:
winget install Microsoft.DotNet.SDK.10

# Clone repository
git clone https://github.com/Ozark-Connect/NetworkOptimizer.git
cd NetworkOptimizer

# Build
dotnet publish src/NetworkOptimizer.Web -c Release -r win-x64 --self-contained -o C:\NetworkOptimizer
```

### Create Startup Script

Create `C:\NetworkOptimizer\start.bat`:

```batch
@echo off
cd /d "%~dp0"

set TZ=America/Chicago
set ASPNETCORE_URLS=http://0.0.0.0:8042

REM Host IP - required for iperf3 client result tracking
set HOST_IP=192.168.1.100

REM Enable iperf3 server for client speed testing (port 5201)
set Iperf3Server__Enabled=true

REM Optional: Set admin password
REM set APP_PASSWORD=your-secure-password

NetworkOptimizer.Web.exe
```

### Install as Windows Service

Use [NSSM](https://nssm.cc/) (Non-Sucking Service Manager):

```powershell
# Download NSSM
Invoke-WebRequest -Uri "https://nssm.cc/release/nssm-2.24.zip" -OutFile "nssm.zip"
Expand-Archive -Path "nssm.zip" -DestinationPath "C:\nssm" -Force

# Install service
C:\nssm\nssm-2.24\win64\nssm.exe install NetworkOptimizer "C:\NetworkOptimizer\start.bat"

# Configure service
C:\nssm\nssm-2.24\win64\nssm.exe set NetworkOptimizer AppDirectory "C:\NetworkOptimizer"
C:\nssm\nssm-2.24\win64\nssm.exe set NetworkOptimizer DisplayName "Network Optimizer"
C:\nssm\nssm-2.24\win64\nssm.exe set NetworkOptimizer Description "UniFi Network Optimizer and Auditor"
C:\nssm\nssm-2.24\win64\nssm.exe set NetworkOptimizer Start SERVICE_AUTO_START

# Start service
net start NetworkOptimizer
```

### Service Management

```powershell
# Check status
Get-Service NetworkOptimizer

# Stop
Stop-Service NetworkOptimizer

# Start
Start-Service NetworkOptimizer

# Restart
Restart-Service NetworkOptimizer
```

### Data Location

- **Database:** `%LOCALAPPDATA%\NetworkOptimizer\network_optimizer.db`
- **Credentials:** `%LOCALAPPDATA%\NetworkOptimizer\.credential_key`

---

## Client Speed Testing (Native Limitation)

**Docker Advantage:** Docker deployment includes bundled OpenSpeedTest for browser-based speed testing from any device (phones, tablets, laptops). This is not available with native deployment.

Native deployments can run iperf3 server mode for CLI-based client speed testing, but this requires iperf3 installed on client devices.

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

**Note:** OpenSpeedTest (browser-based) is only available with Docker deployment. For native deployments, use iperf3 clients or run a separate OpenSpeedTest instance and configure it to POST to `http://your-server:8042/api/speedtest/results`.

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
