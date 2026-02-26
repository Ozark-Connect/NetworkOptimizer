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
# Timezone
export TZ="America/Chicago"

# Optional: Set admin password (auto-generated on first run if not set)
# export APP_PASSWORD="your-secure-password"
```

Additional environment variables can be added to `start.sh` - see [docker/.env.example](../docker/.env.example) for all available options including:
- `HOST_NAME` - Hostname for canonical URL enforcement
- `REVERSE_PROXIED_HOST_NAME` - Hostname when behind a reverse proxy (enables HTTPS)
- `OPENSPEEDTEST_HTTPS` - Enable HTTPS for speed tests (required for geolocation)
- `Logging__LogLevel__NetworkOptimizer` / `Logging__LogLevel__Default` - Logging verbosity (see [Enable Debug Logging](#enable-debug-logging))

Note: The app auto-detects its IP address, so `HOST_IP` is not required for native installations.

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

## Logs and Debugging

Application logs are in `~/network-optimizer/logs/`:

```bash
# Follow live logs
tail -f ~/network-optimizer/logs/stdout.log

# View errors
tail -f ~/network-optimizer/logs/stderr.log

# Search for specific events
grep "UniFi" ~/network-optimizer/logs/stdout.log | tail -20
```

### Enable Debug Logging

For more detailed logs, edit `~/network-optimizer/start.sh` and add:

```bash
# Debug logging for Network Optimizer application code only (recommended):
export Logging__LogLevel__NetworkOptimizer=Debug

# Or debug everything (verbose - includes framework/EF Core noise):
export Logging__LogLevel__Default=Debug
```

Then restart the service:

```bash
launchctl unload ~/Library/LaunchAgents/net.ozarkconnect.networkoptimizer.plist
launchctl load ~/Library/LaunchAgents/net.ozarkconnect.networkoptimizer.plist
```

Remember to set it back to `Information` when done - debug logging is verbose.

### Log Rotation

Logs are not rotated automatically. To clear them:

```bash
# Truncate without restarting
: > ~/network-optimizer/logs/stdout.log
: > ~/network-optimizer/logs/stderr.log
```

## Troubleshooting

### Previous sudo Installation

If you previously ran the install script with `sudo`, files and processes end up owned by root, which breaks future installs and upgrades. The install script detects this automatically and offers to fix it. Just run the script normally (without sudo):

```bash
./scripts/install-macos-native.sh
```

It will prompt for your password once to clean up root-owned files and kill root-owned processes, then continue the installation as your regular user.

**Never run the install script with sudo.** Everything installs to your home directory and does not need root access.

### Reset Admin Password

If you forget the admin password, use the reset script:

```bash
curl -fsSL https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/reset-password.sh | bash
```

The script auto-detects the macOS native installation, clears the password, restarts the service, and displays the new temporary password.

**Manual fallback:**

```bash
# Stop the service
launchctl unload ~/Library/LaunchAgents/net.ozarkconnect.networkoptimizer.plist

# Clear the password
sqlite3 ~/Library/Application\ Support/NetworkOptimizer/network_optimizer.db \
    "UPDATE AdminSettings SET Password = NULL, Enabled = 0;"

# Restart
launchctl load ~/Library/LaunchAgents/net.ozarkconnect.networkoptimizer.plist

# View the new password
grep "Password:" ~/network-optimizer/logs/stdout.log | tail -1
```

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
