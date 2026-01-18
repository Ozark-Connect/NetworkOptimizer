# macOS Native Installation

Install Network Optimizer natively on macOS for maximum performance. Native installation is recommended over Docker Desktop, which limits network throughput to ~1.8 Gbps.

## Quick Start

```bash
git clone https://github.com/Ozark-Connect/NetworkOptimizer.git
cd NetworkOptimizer
./scripts/install-macos-native.sh
```

The script will:
1. Install prerequisites via Homebrew (iperf3, nginx, .NET SDK)
2. Build the application from source
3. Sign binaries for macOS
4. Set up OpenSpeedTest with nginx for browser-based speed testing
5. Create a launchd service for auto-start

## Configuration

After installation, edit `~/network-optimizer/start.sh` to configure environment variables:

```bash
# Host IP - required for speed test result tracking and path analysis
export HOST_IP="192.168.1.100"  # Your Mac's IP address (auto-detected during install)

# Timezone
export TZ="America/Chicago"

# Optional: Set admin password (auto-generated on first run if not set)
# export APP_PASSWORD="your-secure-password"
```

Additional environment variables can be added to `start.sh` - see [docker/.env.example](../docker/.env.example) for all available options including:
- `HOST_NAME` - Hostname for canonical URL enforcement
- `REVERSE_PROXIED_HOST_NAME` - Hostname when behind a reverse proxy (enables HTTPS)
- `OPENSPEEDTEST_HTTPS` - Enable HTTPS for speed tests (required for geolocation)
- `LOG_LEVEL` / `APP_LOG_LEVEL` - Logging verbosity

After editing, restart the service:

```bash
launchctl unload ~/Library/LaunchAgents/net.ozarkconnect.networkoptimizer.plist
launchctl load ~/Library/LaunchAgents/net.ozarkconnect.networkoptimizer.plist
```

## Access

- **Web UI**: http://localhost:8042 or http://\<your-mac-ip\>:8042
- **SpeedTest**: http://localhost:3005 or http://\<your-mac-ip\>:3005

On first run, check the logs for the auto-generated admin password:

```bash
grep -A5 'AUTO-GENERATED' ~/network-optimizer/logs/stdout.log
```

## Service Management

```bash
# Stop
launchctl unload ~/Library/LaunchAgents/net.ozarkconnect.networkoptimizer.plist

# Start
launchctl load ~/Library/LaunchAgents/net.ozarkconnect.networkoptimizer.plist

# View logs
tail -f ~/network-optimizer/logs/stdout.log
```

## Upgrading

To upgrade to a newer version:

```bash
cd NetworkOptimizer
git pull
./scripts/install-macos-native.sh
```

The install script preserves your database, encryption keys, and `start.sh` configuration by backing them up before reinstalling.

## Uninstalling

```bash
# Stop and remove the service
launchctl unload ~/Library/LaunchAgents/net.ozarkconnect.networkoptimizer.plist
rm ~/Library/LaunchAgents/net.ozarkconnect.networkoptimizer.plist

# Remove application files
rm -rf ~/network-optimizer

# Remove data (database, keys) - optional
rm -rf ~/Library/Application\ Support/NetworkOptimizer
```
